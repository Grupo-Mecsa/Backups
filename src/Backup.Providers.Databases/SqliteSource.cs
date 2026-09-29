using Backup.Application.Providers;
using Microsoft.Data.Sqlite;

namespace Backup.Providers.Databases;

/// <summary>Copia consistente de una base SQLite usando la API de backup en línea.</summary>
public sealed class SqliteSource : IBackupSource, IConnectionTester
{
    public ProviderDescriptor Descriptor { get; } = new(
        "sqlite",
        "SQLite",
        "Copia en caliente y consistente de un archivo SQLite.",
        ProviderCategory.Database,
        "database",
        [
            SettingField.Text("path", "Ruta del archivo .db", required: true, placeholder: "/data/app.db"),
        ]);

    public Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var sourcePath = context.Settings.Require("path");
        EnsureExists(sourcePath);

        var output = Path.Combine(context.WorkingDirectory, context.BaseName + ".db");
        context.Log.Info($"Copiando {sourcePath} con la API de backup de SQLite...");

        using (var source = Open(sourcePath, SqliteOpenMode.ReadOnly))
        using (var destination = Open(output, SqliteOpenMode.ReadWriteCreate))
        {
            source.BackupDatabase(destination);
        }

        SqliteConnection.ClearAllPools();
        return Task.FromResult(new BackupArtifact(output, ".db"));
    }

    public async Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var path = settings.Require("path");
        EnsureExists(path);
        await using var connection = Open(path, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table'";
        var tables = await command.ExecuteScalarAsync(cancellationToken);
        return $"Archivo válido con {tables} tablas.";
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No existe el archivo {path}.");
        }
    }
}
