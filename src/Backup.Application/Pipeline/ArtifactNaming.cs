using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Backup.Application.Pipeline;

/// <summary>
/// Convención de nombres de los respaldos en el destino:
/// <c>{slug}/{slug}_{yyyyMMdd_HHmmss}{extensiones}</c>.
/// El timestamp embebido permite aplicar retención sin depender de metadatos del destino.
/// </summary>
public static partial class ArtifactNaming
{
    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    public static string Slug(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        var slug = MultipleDashes().Replace(builder.ToString(), "-").Trim('-');
        return slug.Length == 0 ? "backup" : slug;
    }

    public static string BaseName(string jobName, DateTimeOffset timestamp) =>
        $"{Slug(jobName)}_{timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}";

    public static string Folder(string jobName) => Slug(jobName);

    public static string ObjectName(string jobName, DateTimeOffset timestamp, string extensions) =>
        $"{Folder(jobName)}/{BaseName(jobName, timestamp)}{extensions}";

    public static DateTimeOffset? TryParseTimestamp(string objectName)
    {
        var match = TimestampPattern().Match(Path.GetFileName(objectName));
        return match.Success && DateTime.TryParseExact(
            match.Groups[1].Value, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultipleDashes();

    [GeneratedRegex(@"_(\d{8}_\d{6})(?:\.|$)")]
    private static partial Regex TimestampPattern();
}
