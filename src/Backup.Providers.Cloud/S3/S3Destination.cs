using Amazon.S3;
using Amazon.S3.Transfer;
using Backup.Application.Providers;

namespace Backup.Providers.Cloud.S3;

public sealed class S3Destination : IBackupDestination, IConnectionTester, IFolderBrowser, IArtifactReader
{
    public ProviderDescriptor Descriptor { get; } = new(
        "s3",
        "Amazon S3 / compatible",
        "AWS S3, MinIO, Cloudflare R2, Wasabi, Backblaze B2 y cualquier servicio compatible con S3.",
        ProviderCategory.Cloud,
        "cloud",
        S3Connection.Fields(includeStorageClass: true));

    public async Task DownloadAsync(DestinationContext context, string objectName, string localFile, CancellationToken cancellationToken)
    {
        using var client = S3Connection.CreateClient(context.Settings);
        using var response = await client.GetObjectAsync(context.Settings.Require("bucket"), S3Connection.Key(context.Settings, objectName), cancellationToken);
        await response.WriteResponseStreamToFileAsync(localFile, append: false, cancellationToken);
    }

    public async Task UploadAsync(DestinationContext context, string localFile, string objectName, CancellationToken cancellationToken)
    {
        using var client = S3Connection.CreateClient(context.Settings);
        if (context.Settings.GetBool("createBucket"))
        {
            await EnsureBucketAsync(client, context.Settings.Require("bucket"), cancellationToken);
        }

        using var transfer = new TransferUtility(client);
        var request = new TransferUtilityUploadRequest
        {
            BucketName = context.Settings.Require("bucket"),
            Key = S3Connection.Key(context.Settings, objectName),
            FilePath = localFile,
            StorageClass = S3StorageClass.FindValue(context.Settings.Get("storageClass", "STANDARD")),
        };
        await transfer.UploadAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredBackup>> ListAsync(DestinationContext context, string folder, CancellationToken cancellationToken)
    {
        using var client = S3Connection.CreateClient(context.Settings);
        var root = context.Settings.Get("prefix");
        var prefix = S3Connection.Key(context.Settings, folder).TrimEnd('/') + "/";
        var result = new List<StoredBackup>();
        await foreach (var item in S3Connection.ListAllAsync(client, context.Settings.Require("bucket"), prefix, cancellationToken))
        {
            result.Add(new StoredBackup(ProviderHelpers.MakeRelative(root, item.Key), item.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) : null, item.Size));
        }

        return result;
    }

    public async Task DeleteAsync(DestinationContext context, string objectName, CancellationToken cancellationToken)
    {
        using var client = S3Connection.CreateClient(context.Settings);
        await client.DeleteObjectAsync(context.Settings.Require("bucket"), S3Connection.Key(context.Settings, objectName), cancellationToken);
    }

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        S3Connection.BrowseAsync(settings, path, cancellationToken);

    public Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        S3Connection.TestAsync(settings, cancellationToken);

    private static async Task EnsureBucketAsync(IAmazonS3 client, string bucket, CancellationToken cancellationToken)
    {
        try
        {
            await client.PutBucketAsync(bucket, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Ya existe.
        }
    }
}
