using Backup.Application.Providers;

namespace Backup.Providers.Cloud.S3;

/// <summary>Descarga los objetos bajo un prefijo y los empaqueta en un .zip.</summary>
/// <param name="variant">Servicio compatible concreto (R2, B2...); null = S3 genérico.</param>
public sealed class S3Source(ProviderVariant? variant = null) : IBackupSource, IConnectionTester, IFolderBrowser
{
    public ProviderDescriptor Descriptor { get; } = variant is null
        ? BaseDescriptor
        : variant.Apply(BaseDescriptor, $"Respalda el contenido de un bucket de {variant.DisplayName} en un .zip.");

    private static readonly ProviderDescriptor BaseDescriptor = new(
        "s3",
        "Amazon S3 / compatible",
        "Respalda el contenido de un bucket o prefijo S3 en un .zip.",
        ProviderCategory.Cloud,
        "cloud",
        [
            .. S3Connection.Fields(includeStorageClass: false),
            SettingField.Selection("selection", "prefix"),
            SettingField.Text("include", "Incluir por patrón", placeholder: "*.pdf, *.xlsx", help: "Patrones separados por coma. Vacío = todo."),
        ]);

    public string? ArtifactExtension(ProviderSettings settings) => ".zip";

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = Settings(context.Settings);
        var bucket = settings.Require("bucket");
        var root = settings.Get("prefix");
        var prefix = string.IsNullOrEmpty(root) ? string.Empty : root.TrimEnd('/') + "/";
        var include = ProviderHelpers.GlobFilter(settings.GetList("include"));
        var selection = PathSelection.Parse(settings.GetRaw("selection"));
        var staging = Directory.CreateDirectory(Path.Combine(context.WorkingDirectory, "s3")).FullName;

        using var client = S3Connection.CreateClient(settings);
        var count = 0;
        await foreach (var item in S3Connection.ListAllAsync(client, bucket, prefix, cancellationToken))
        {
            if (item.Key.EndsWith('/') || !include(Path.GetFileName(item.Key)) || !selection.IsIncluded(PathSelection.Relative(root, item.Key)))
            {
                continue;
            }

            var relative = ProviderHelpers.MakeRelative(root, item.Key);
            var target = Path.GetFullPath(Path.Combine(staging, relative));
            if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue; // Protección contra claves con "../"
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var response = await client.GetObjectAsync(bucket, item.Key, cancellationToken);
            await response.WriteResponseStreamToFileAsync(target, append: false, cancellationToken);
            count++;
        }

        context.Log.Info($"{count} objetos descargados de s3://{bucket}/{prefix}");
        return await ProviderHelpers.ZipDirectoryAsync(staging, context, cancellationToken);
    }

    public Task<FolderListing> BrowseAsync(ProviderSettings settings, string? path, CancellationToken cancellationToken) =>
        S3Connection.BrowseAsync(Settings(settings), path, cancellationToken);

    public Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        S3Connection.TestAsync(Settings(settings), cancellationToken);

    private ProviderSettings Settings(ProviderSettings settings) => variant?.Apply(settings) ?? settings;
}
