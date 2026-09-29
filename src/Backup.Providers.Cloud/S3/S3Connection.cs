using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Backup.Application.Providers;

namespace Backup.Providers.Cloud.S3;

/// <summary>Configuración y operaciones comunes a origen y destino S3.</summary>
internal static class S3Connection
{
    public static IReadOnlyList<SettingField> Fields(bool includeStorageClass) =>
    [
        SettingField.Text("bucket", "Bucket", required: true),
        SettingField.Text("region", "Región", defaultValue: "us-east-1"),
        SettingField.Text("accessKey", "Access key", help: "Vacío = credenciales del entorno (IAM role, variables AWS_*)."),
        SettingField.Secret("secretKey", "Secret key"),
        SettingField.Text("prefix", "Prefijo / carpeta", placeholder: "backups/produccion").Browsable(),
        SettingField.Text("serviceUrl", "Endpoint personalizado", placeholder: "https://minio.local:9000",
            help: "Para servicios compatibles: MinIO, Cloudflare R2, Wasabi, Backblaze B2, DigitalOcean Spaces..."),
        SettingField.Toggle("forcePathStyle", "Path-style (requerido por MinIO)", false),
        .. includeStorageClass
            ? new[]
            {
                SettingField.Select("storageClass", "Clase de almacenamiento",
                    ["STANDARD", "STANDARD_IA", "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER_IR", "GLACIER", "DEEP_ARCHIVE"], "STANDARD",
                    "Algunos servicios compatibles solo aceptan STANDARD."),
                SettingField.Toggle("createBucket", "Crear el bucket si no existe", false),
            }
            : [],
    ];

    public static AmazonS3Client CreateClient(ProviderSettings settings)
    {
        var config = new AmazonS3Config { ForcePathStyle = settings.GetBool("forcePathStyle") };
        if (settings.Get("serviceUrl") is { } serviceUrl)
        {
            config.ServiceURL = serviceUrl;
            config.AuthenticationRegion = settings.Get("region", "us-east-1");
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Get("region", "us-east-1"));
        }

        return settings.Get("accessKey") is { } accessKey
            ? new AmazonS3Client(new BasicAWSCredentials(accessKey, settings.GetRaw("secretKey") ?? string.Empty), config)
            : new AmazonS3Client(config);
    }

    public static string Key(ProviderSettings settings, string relative) =>
        ProviderHelpers.CombineRemote(settings.Get("prefix"), relative);

    public static async IAsyncEnumerable<S3Object> ListAllAsync(
        IAmazonS3 client, string bucket, string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                yield return item;
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    /// <summary>Lista un "nivel" del bucket usando '/' como delimitador. Rutas sin '/' inicial.</summary>
    public static async Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken)
    {
        using var client = CreateClient(settings);
        var current = (path ?? string.Empty).Trim('/');
        var request = new ListObjectsV2Request
        {
            BucketName = settings.Require("bucket"),
            Prefix = current.Length == 0 ? string.Empty : current + "/",
            Delimiter = "/",
        };

        var items = new List<FolderItem>();
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);
            items.AddRange((response.CommonPrefixes ?? []).Select(p => p.TrimEnd('/')).Select(p => new FolderItem(p[(p.LastIndexOf('/') + 1)..], p, true)));
            items.AddRange((response.S3Objects ?? [])
                .Where(o => !o.Key.EndsWith('/'))
                .Select(o => new FolderItem(o.Key[(o.Key.LastIndexOf('/') + 1)..], o.Key, false, o.Size,
                    o.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) : null)));
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true && items.Count < 5000);

        return FolderListing.Create(current, FolderListing.SlashParent(current, rooted: false), items);
    }

    public static async Task<string> TestAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var client = CreateClient(settings);
        var bucket = settings.Require("bucket");
        try
        {
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1, Prefix = settings.Get("prefix") ?? string.Empty }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
            return settings.GetBool("createBucket")
                ? $"Conexión correcta. El bucket '{bucket}' no existe y se creará en el primer respaldo."
                : throw new InvalidOperationException($"Conexión correcta, pero el bucket '{bucket}' no existe. Créalo o activa \"Crear el bucket si no existe\".");
        }

        return $"Acceso correcto al bucket '{bucket}'.";
    }
}
