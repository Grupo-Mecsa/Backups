using System.Text.RegularExpressions;

namespace Backup.Application.Runs;

public enum LogLevelKind { Info, Warning, Omitted, Error }

/// <summary>Una entrada de la bitácora: la línea con hora y nivel, más las líneas de detalle que le siguen.</summary>
public sealed record LogEntry(int Index, string Time, LogLevelKind Level, string Message, IReadOnlyList<string> Details)
{
    /// <summary>Texto completo (mensaje y detalle), para buscar.</summary>
    public bool Matches(string term) =>
        Message.Contains(term, StringComparison.CurrentCultureIgnoreCase)
        || Details.Any(d => d.Contains(term, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Un elemento omitido: ruta y causa.</summary>
public sealed record OmittedItem(string Path, string Reason, int EntryIndex)
{
    public string Name => Path.TrimEnd('/') is var p && p.LastIndexOf('/') is var i and >= 0 ? p[(i + 1)..] : Path;

    public string Folder => Path.TrimEnd('/') is var p && p.LastIndexOf('/') is var i and > 0 ? p[..i] : string.Empty;
}

/// <summary>Elementos omitidos por la misma causa.</summary>
public sealed record OmittedGroup(string Reason, string? Hint, IReadOnlyList<OmittedItem> Items);

/// <summary>Bitácora interpretada: entradas y resumen de lo omitido.</summary>
public sealed record ParsedLog(IReadOnlyList<LogEntry> Entries, IReadOnlyList<OmittedGroup> Omitted)
{
    public int Count(LogLevelKind level) => Entries.Count(e => e.Level == level);
}

/// <summary>
/// Interpreta el texto de una bitácora ("HH:mm:ss NIV mensaje" y líneas de detalle). Reconoce también las bitácoras
/// anteriores al nivel OMI, en las que lo omitido se registraba como WRN "Omitido …".
/// </summary>
public static partial class LogParser
{
    public static ParsedLog Parse(string? log)
    {
        var entries = new List<LogEntry>();
        string? time = null;
        var level = LogLevelKind.Info;
        string? message = null;
        var details = new List<string>();

        void Flush()
        {
            if (message is not null)
            {
                entries.Add(new LogEntry(entries.Count, time ?? string.Empty, level, message, [.. TrimBlank(details)]));
            }

            details = [];
        }

        foreach (var raw in (log ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = LinePattern().Match(line);
            if (match.Success)
            {
                Flush();
                time = match.Groups["time"].Value;
                message = match.Groups["msg"].Value;
                level = match.Groups["lvl"].Value switch
                {
                    "ERR" => LogLevelKind.Error,
                    "OMI" => LogLevelKind.Omitted,
                    "WRN" when IsOmission(message) => LogLevelKind.Omitted,
                    "WRN" => LogLevelKind.Warning,
                    _ => LogLevelKind.Info,
                };
            }
            else if (message is not null)
            {
                details.Add(line);
            }
            else if (line.Length > 0)
            {
                // Texto sin formato al principio: se muestra como una entrada informativa.
                message = line;
            }
        }

        Flush();
        return new ParsedLog(entries, GroupOmitted(entries));
    }

    private static bool IsOmission(string message) =>
        message.StartsWith("Omitido", StringComparison.Ordinal) || message.StartsWith("Carpeta omitida", StringComparison.Ordinal);

    private static IReadOnlyList<OmittedGroup> GroupOmitted(IEnumerable<LogEntry> entries) =>
        [.. entries.Where(e => e.Level == LogLevelKind.Omitted)
            .Select(ToItem)
            .GroupBy(i => i.Reason)
            .Select(g => new OmittedGroup(g.Key, HintFor(g.Key), [.. g]))
            .OrderByDescending(g => g.Items.Count)];

    private static OmittedItem ToItem(LogEntry entry)
    {
        var folder = FolderPattern().Match(entry.Message);
        if (folder.Success)
        {
            return new OmittedItem(folder.Groups["path"].Value, Friendly(folder.Groups["reason"].Value), entry.Index);
        }

        var file = FilePattern().Match(entry.Message);
        return file.Success
            ? new OmittedItem(file.Groups["path"].Value, Friendly(file.Groups["reason"].Value), entry.Index)
            : new OmittedItem(entry.Message, "Otros", entry.Index);
    }

    /// <summary>Causa legible para agrupar (las que traen la ruta repetida se reducen a una frase común).</summary>
    private static string Friendly(string reason)
    {
        if (reason.Contains("pero responde que no existe", StringComparison.Ordinal))
        {
            return "El servidor los lista, pero no deja descargarlos";
        }

        if (reason.Contains("no deja abrirla", StringComparison.Ordinal))
        {
            return "Carpetas que el servidor lista, pero no deja abrir";
        }

        reason = reason.Trim().TrimEnd('.');
        return reason.Length == 0 ? "Otros" : char.ToUpperInvariant(reason[0]) + reason[1..];
    }

    private static string? HintFor(string reason) => reason switch
    {
        "El servidor los lista, pero no deja descargarlos" =>
            "Suele pasar con nombres que llevan Ó, Á, Í, ’ u otros caracteres especiales: el servidor FTP no los acepta de vuelta. Lee esa carpeta por SMB o SFTP, o renombra los archivos.",
        "Carpetas que el servidor lista, pero no deja abrir" =>
            "Revisa los permisos de la cuenta sobre esas carpetas o los caracteres especiales en su nombre.",
        _ => null,
    };

    private static IEnumerable<string> TrimBlank(List<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        return lines.Take(end);
    }

    [GeneratedRegex(@"^(?<time>\d{2}:\d{2}:\d{2}) (?<lvl>INF|WRN|ERR|OMI) (?<msg>.*)$")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^Omitido (?<path>.+?): (?<reason>.*)$")]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"^Carpeta omitida \((?<reason>[^)]*)\): (?<path>.+)$")]
    private static partial Regex FolderPattern();
}
