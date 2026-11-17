using System.Xml.Linq;
using Backup.Application;
using Backup.Application.Abstractions;
using Backup.Application.Providers;
using Backup.Providers.Cloud;
using Backup.Providers.Cloud.S3;
using Backup.Providers.Databases;
using Backup.Providers.Files;
using Backup.Providers.Files.WebDav;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Tests;

public class ProviderVariantTests
{
    private static IProviderRegistry Registry()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProcessRunner>(new NoProcesses())
            .AddDatabaseProviders()
            .AddCloudProviders()
            .AddFileProviders()
            .AddSingleton<IProviderRegistry, ProviderRegistry>()
            .BuildServiceProvider();
        return services.GetRequiredService<IProviderRegistry>();
    }

    [Theory]
    [InlineData("supabase")]
    [InlineData("neon")]
    [InlineData("azurepostgres")]
    [InlineData("azuremysql")]
    [InlineData("mongoatlas")]
    [InlineData("azuresql")]
    [InlineData("r2")]
    [InlineData("b2")]
    [InlineData("gcs")]
    [InlineData("supabase-storage")]
    [InlineData("azurefiles")]
    [InlineData("googledrive")]
    [InlineData("onedrive")]
    [InlineData("dropbox")]
    [InlineData("webdav")]
    public void Registry_ExposesNewProviders_AsSource(string key)
    {
        var source = Registry().GetSource(key);
        Assert.True(source.Descriptor.SupportsConnections);
    }

    [Fact]
    public void Registry_KeysAreUnique_AndGenericProvidersKeepTheirKeys()
    {
        var registry = Registry();
        Assert.Equal(registry.Sources.Count, registry.Sources.Select(s => s.Descriptor.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("PostgreSQL", registry.GetSource("postgres").Descriptor.DisplayName);
        Assert.Equal("Amazon S3 / compatible", registry.GetDestination("s3").Descriptor.DisplayName);
    }

    [Fact]
    public void SupabaseVariant_ChangesDefaults_ButKeepsFieldKeys()
    {
        var supabase = Registry().GetSource("supabase").Descriptor;
        var postgres = Registry().GetSource("postgres").Descriptor;

        Assert.Equal(postgres.Fields.Select(f => f.Key), supabase.Fields.Select(f => f.Key));
        Assert.Equal("require", supabase.Fields.Single(f => f.Key == "sslMode").DefaultValue);
        Assert.Equal("public", supabase.Fields.Single(f => f.Key == "schemas").DefaultValue);
        Assert.Null(supabase.Fields.Single(f => f.Key == "user").DefaultValue);
    }

    [Fact]
    public void SupabaseVariant_IsAlsoARestoreTarget_ForPostgresDumps()
    {
        var targets = Registry().RestoreTargetsFor("erp_20260101.dump").Select(t => t.Descriptor.Key).ToList();
        Assert.Contains("postgres", targets);
        Assert.Contains("supabase", targets);
        Assert.Contains("azuresql", Registry().RestoreTargetsFor("erp.bacpac").Select(t => t.Descriptor.Key));
    }

    [Fact]
    public void R2Variant_HidesEndpoint_AndBuildsItFromAccountId()
    {
        var descriptor = new S3Destination(S3Variants.CloudflareR2).Descriptor;
        Assert.DoesNotContain(descriptor.Fields, f => f.Key is "serviceUrl" or "region" or "forcePathStyle");
        Assert.Equal("accountId", descriptor.Fields[0].Key);
        Assert.Equal(["STANDARD", "STANDARD_IA"], descriptor.Fields.Single(f => f.Key == "storageClass").Options);

        var settings = S3Variants.CloudflareR2.Apply(Settings(("accountId", "abc123"), ("jurisdiction", "eu")));
        Assert.Equal("https://abc123.eu.r2.cloudflarestorage.com", settings.Get("serviceUrl"));
        Assert.Equal("auto", settings.Get("region"));
        Assert.True(settings.GetBool("forcePathStyle"));
    }

    [Fact]
    public void S3Variants_WithoutStorageClasses_DropTheField()
    {
        Assert.DoesNotContain(new S3Destination(S3Variants.BackblazeB2).Descriptor.Fields, f => f.Key == "storageClass");
        Assert.Equal("https://s3.us-west-004.backblazeb2.com",
            S3Variants.BackblazeB2.Apply(Settings(("region", "us-west-004"))).Get("serviceUrl"));
        Assert.Equal("https://abcd.storage.supabase.co/storage/v1/s3",
            S3Variants.SupabaseStorage.Apply(Settings(("projectRef", "abcd"), ("region", "us-east-1"))).Get("serviceUrl"));
    }

    [Fact]
    public void WebDavParse_SkipsSelf_AndReadsFilesAndFolders()
    {
        var xml = XDocument.Parse("""
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/remote.php/dav/files/ana/Respaldos/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/remote.php/dav/files/ana/Respaldos/Facturas%202026/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>https://nube.local/remote.php/dav/files/ana/Respaldos/a%C3%B1o.pdf</d:href>
                <d:propstat><d:prop><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength>
                <d:getlastmodified>Tue, 17 Nov 2026 10:00:00 GMT</d:getlastmodified></d:prop></d:propstat></d:response>
            </d:multistatus>
            """);

        var entries = WebDavSessionFactory.Parse(xml, new Uri("https://nube.local/remote.php/dav/files/ana/Respaldos/")).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e is { Name: "Facturas 2026", IsDirectory: true });
        var file = entries.Single(e => !e.IsDirectory);
        Assert.Equal("año.pdf", file.Name);
        Assert.Equal(1234, file.Size);
        Assert.NotNull(file.Modified);
    }

    private static ProviderSettings Settings(params (string Key, string? Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value));

    private sealed class NoProcesses : IProcessRunner
    {
        public Task RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdoutFile = null,
            IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
