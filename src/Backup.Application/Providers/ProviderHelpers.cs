using System.IO.Compression;

namespace Backup.Application.Providers;

/// <summary>Utilidades compartidas por las implementaciones de proveedores.</summary>
public static class ProviderHelpers
{
    /// <summary>Une una ruta raíz remota con un nombre de objeto usando '/'.</summary>
    public static string CombineRemote(string? root, string relative)
    {
        var cleanRoot = (root ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        var cleanRelative = relative.Replace('\\', '/').TrimStart('/');
        return cleanRoot.Length == 0 ? cleanRelative : $"{cleanRoot}/{cleanRelative}";
    }

    /// <summary>Devuelve <paramref name="fullPath"/> relativo a <paramref name="root"/> (ambos con '/').</summary>
    public static string MakeRelative(string? root, string fullPath)
    {
        var cleanRoot = (root ?? string.Empty).Replace('\\', '/').Trim('/');
        var cleanPath = fullPath.Replace('\\', '/').TrimStart('/');
        return cleanRoot.Length > 0 && cleanPath.StartsWith(cleanRoot + "/", StringComparison.Ordinal)
            ? cleanPath[(cleanRoot.Length + 1)..]
            : cleanPath;
    }

    /// <summary>Comprime un directorio en un .zip dentro del directorio de trabajo.</summary>
    public static async Task<BackupArtifact> ZipDirectoryAsync(
        string directory, SourceContext context, CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(context.WorkingDirectory, context.BaseName + ".zip");
        await ZipFile.CreateFromDirectoryAsync(directory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false, cancellationToken);
        return new BackupArtifact(zipPath, ".zip");
    }

    /// <summary>Filtro simple por comodines (*, ?) separado por comas. Lista vacía = acepta todo.</summary>
    public static Func<string, bool> GlobFilter(IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return _ => true;
        }

        var regexes = patterns
            .Select(p => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .ToList();
        return name => regexes.Any(r => r.IsMatch(name));
    }
}
