using Backup.Application.Abstractions;

namespace Backup.Providers.Databases;

/// <summary>Consultas cortas con el cliente de línea de comandos del motor (psql, mysql) para listar valores.</summary>
internal static class ClientTools
{
    /// <summary>
    /// Cliente junto a la herramienta de volcado configurada: "/opt/pg/bin/pg_dump" → "/opt/pg/bin/psql".
    /// Si la ruta no es la herramienta esperada, se usa el cliente del PATH.
    /// </summary>
    public static string Sibling(string? dumpToolPath, string dumpTool, string client)
    {
        if (string.IsNullOrWhiteSpace(dumpToolPath)
            || !string.Equals(Path.GetFileNameWithoutExtension(dumpToolPath), dumpTool, StringComparison.OrdinalIgnoreCase))
        {
            return client;
        }

        var directory = Path.GetDirectoryName(dumpToolPath);
        var file = client + Path.GetExtension(dumpToolPath);
        return string.IsNullOrEmpty(directory) ? file : Path.Combine(directory, file);
    }

    /// <summary>Ejecuta el cliente y devuelve las líneas no vacías de su salida.</summary>
    public static async Task<IReadOnlyList<string>> ReadLinesAsync(
        IProcessRunner processes,
        string tool,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var output = Path.Combine(Path.GetTempPath(), $"backup-list-{Guid.NewGuid():N}.txt");
        try
        {
            await processes.RunAsync(tool, arguments, output, environment, cancellationToken);
            var lines = await File.ReadAllLinesAsync(output, cancellationToken);
            return [.. lines.Select(l => l.Trim()).Where(l => l.Length > 0)];
        }
        finally
        {
            File.Delete(output);
        }
    }
}
