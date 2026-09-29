using System.Security.Cryptography;
using Backup.Application.Pipeline;
using Backup.Application.Providers;
using Backup.Domain.Jobs;
using Backup.Infrastructure.Transforms;

namespace Backup.Tests;

public class PipelineUnitTests
{
    [Fact]
    public async Task Encryption_RoundTrip_RestoresOriginalBytes()
    {
        var original = RandomNumberGenerator.GetBytes(300_000);
        using var encrypted = new MemoryStream();
        await using (var writer = BackupEncryption.CreateEncryptingStream(encrypted, "clave-super-secreta"))
        {
            await writer.WriteAsync(original);
        }

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        await BackupEncryption.DecryptAsync(encrypted, decrypted, "clave-super-secreta");

        Assert.Equal(original, decrypted.ToArray());
    }

    [Fact]
    public async Task Encryption_WrongPassphraseOrTampering_IsRejected()
    {
        using var encrypted = new MemoryStream();
        await using (var writer = BackupEncryption.CreateEncryptingStream(encrypted, "correcta123"))
        {
            await writer.WriteAsync("datos importantes"u8.ToArray());
        }

        encrypted.Position = 0;
        await Assert.ThrowsAsync<CryptographicException>(() => BackupEncryption.DecryptAsync(encrypted, Stream.Null, "incorrecta"));

        var bytes = encrypted.ToArray();
        bytes[40] ^= 0xFF;
        await Assert.ThrowsAsync<CryptographicException>(() => BackupEncryption.DecryptAsync(new MemoryStream(bytes), Stream.Null, "correcta123"));
    }

    [Theory]
    [InlineData("ERP Producción", "erp-produccion")]
    [InlineData("  Nómina / 2026  ", "nomina-2026")]
    [InlineData("***", "backup")]
    public void Slug_IsFileSystemSafe(string input, string expected) =>
        Assert.Equal(expected, ArtifactNaming.Slug(input));

    [Fact]
    public void ObjectName_EmbedsParsableTimestamp()
    {
        var at = new DateTimeOffset(2026, 9, 29, 2, 30, 15, TimeSpan.Zero);
        var name = ArtifactNaming.ObjectName("Mi Job", at, ".sql.gz.enc");

        Assert.Equal("mi-job/mi-job_20260929_023015.sql.gz.enc", name);
        Assert.Equal(at, ArtifactNaming.TryParseTimestamp(name));
    }

    [Fact]
    public void Retention_KeepsLastNAndRecentDays_IgnoresForeignFiles()
    {
        var now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var backups = Enumerable.Range(0, 10)
            .Select(d => new StoredBackup(ArtifactNaming.ObjectName("db", now.AddDays(-d), ".sql"), null, null))
            .Append(new StoredBackup("db/notas-del-admin.txt", null, null))
            .ToList();

        var byCount = RetentionEvaluator.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 3 }, now);
        Assert.Equal(7, byCount.Count);
        Assert.DoesNotContain(byCount, b => b.ObjectName.EndsWith(".txt", StringComparison.Ordinal));

        var combined = RetentionEvaluator.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 2, KeepDays = 5 }, now);
        Assert.Equal(4, combined.Count); // días 6..9

        Assert.Empty(RetentionEvaluator.SelectForDeletion(backups, new RetentionPolicy { KeepLast = 0, KeepDays = 0 }, now));
    }

    [Fact]
    public void Compression_ExtensionMatchesKind()
    {
        var transform = new CompressionTransform();
        Assert.Equal(".gz", transform.GetExtension(new BackupJob { Compression = CompressionKind.GZip }));
        Assert.False(transform.AppliesTo(new BackupJob { Compression = CompressionKind.None }));
    }
}
