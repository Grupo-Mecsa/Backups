using Backup.Application.Providers;
using Microsoft.Data.SqlClient;

namespace Backup.Providers.Databases;

/// <summary>
/// Respaldo nativo de SQL Server (<c>BACKUP DATABASE ... TO DISK</c>). El archivo .bak lo escribe
/// el propio servidor, por eso se necesita una carpeta compartida entre SQL Server y esta aplicación.
/// </summary>
public sealed class SqlServerSource : IBackupSource, IConnectionTester
{
    public ProviderDescriptor Descriptor { get; } = new(
        "sqlserver",
        "SQL Server",
        "Respaldo nativo .bak mediante BACKUP DATABASE.",
        ProviderCategory.Database,
        "database",
        [
            SettingField.Text("server", "Servidor", required: true, placeholder: "sql01,1433 o sql01\\INSTANCIA"),
            SettingField.Text("database", "Base de datos", required: true),
            SettingField.Text("user", "Usuario", help: "Vacío = autenticación integrada."),
            SettingField.Secret("password", "Contraseña"),
            SettingField.Toggle("trustServerCertificate", "Confiar en el certificado del servidor", true),
            SettingField.Text("serverBackupPath", "Carpeta de respaldo (vista por SQL Server)", required: true,
                placeholder: "/var/opt/mssql/backup o D:\\Backups",
                help: "Ruta donde SQL Server escribirá el .bak."),
            SettingField.Text("localBackupPath", "Misma carpeta (vista por esta app)",
                placeholder: "/mnt/sqlbackup",
                help: "Ruta montada en este contenedor que apunta a la carpeta anterior. Vacío = misma ruta."),
            SettingField.Toggle("copyOnly", "COPY_ONLY", true, "No altera la cadena de respaldos diferenciales/log."),
            SettingField.Toggle("nativeCompression", "Compresión nativa de SQL Server", false, "No disponible en la edición Express."),
            SettingField.Number("commandTimeout", "Tiempo máximo (minutos)", 120),
        ]);

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var database = settings.Require("database");
        var fileName = context.BaseName + ".bak";
        var serverPath = JoinServerPath(settings.Require("serverBackupPath"), fileName);
        var localPath = Path.Combine(settings.Get("localBackupPath") ?? settings.Require("serverBackupPath"), fileName);

        var options = new List<string> { "INIT", "FORMAT", "CHECKSUM" };
        if (settings.GetBool("copyOnly", true))
        {
            options.Add("COPY_ONLY");
        }

        if (settings.GetBool("nativeCompression"))
        {
            options.Add("COMPRESSION");
        }

        context.Log.Info($"Ejecutando BACKUP DATABASE [{database}] en el servidor...");
        await using (var connection = new SqlConnection(BuildConnectionString(settings)))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"BACKUP DATABASE @db TO DISK = @path WITH {string.Join(", ", options)}";
            command.CommandTimeout = Math.Max(1, settings.GetInt("commandTimeout", 120)) * 60;
            command.Parameters.AddWithValue("@db", database);
            command.Parameters.AddWithValue("@path", serverPath);
            connection.InfoMessage += (_, e) => context.Log.Info(e.Message);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException(
                $"SQL Server generó el respaldo en '{serverPath}', pero no se encuentra en '{localPath}'. Revisa que ambas rutas apunten a la misma carpeta compartida.");
        }

        var target = Path.Combine(context.WorkingDirectory, fileName);
        File.Move(localPath, target);
        return new BackupArtifact(target, ".bak");
    }

    public async Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(BuildConnectionString(settings));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SERVERPROPERTY('ProductVersion'), SERVERPROPERTY('Edition'), DB_ID(@db)";
        command.Parameters.AddWithValue("@db", settings.Require("database"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.IsDBNull(2))
        {
            throw new InvalidOperationException($"Conectado, pero la base '{settings.Get("database")}' no existe.");
        }

        return $"SQL Server {reader.GetValue(0)} ({reader.GetValue(1)})";
    }

    private static string BuildConnectionString(ProviderSettings settings)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Require("server"),
            InitialCatalog = "master",
            TrustServerCertificate = settings.GetBool("trustServerCertificate", true),
            ApplicationName = "Backup",
            ConnectTimeout = 15,
        };

        if (settings.Get("user") is { } user)
        {
            builder.UserID = user;
            builder.Password = settings.GetRaw("password") ?? string.Empty;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }

    /// <summary>Une con el separador del servidor (que puede ser Windows aunque esta app corra en Linux).</summary>
    private static string JoinServerPath(string folder, string fileName)
    {
        var separator = folder.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        return folder.TrimEnd('\\', '/') + separator + fileName;
    }
}
