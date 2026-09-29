using Backup.Application.Abstractions;
using Backup.Application.Providers;

namespace Backup.Providers.Databases;

/// <summary>Respaldo de PostgreSQL con pg_dump (la versión de pg_dump debe ser ≥ la del servidor).</summary>
public sealed class PostgreSqlSource(IProcessRunner processes) : IBackupSource, IConnectionTester
{
    public ProviderDescriptor Descriptor { get; } = new(
        "postgres",
        "PostgreSQL",
        "Dump lógico con pg_dump. Compatible con Supabase, RDS, Azure Database, etc.",
        ProviderCategory.Database,
        "database",
        [
            SettingField.Text("host", "Host", required: true, placeholder: "db.example.com"),
            SettingField.Number("port", "Puerto", 5432),
            SettingField.Text("database", "Base de datos", required: true),
            SettingField.Text("user", "Usuario", required: true, defaultValue: "postgres"),
            SettingField.Secret("password", "Contraseña"),
            SettingField.Select("format", "Formato", ["custom", "plain", "tar"], "custom",
                "custom: restaurable con pg_restore y ya comprimido. plain: script SQL."),
            SettingField.Select("sslMode", "SSL", ["prefer", "require", "disable", "verify-full"], "prefer"),
            SettingField.Text("schemas", "Esquemas (opcional)", placeholder: "public, ventas", help: "Separados por coma. Vacío = todos."),
            SettingField.Text("extraArgs", "Argumentos adicionales", placeholder: "--no-owner --no-privileges"),
            SettingField.Text("toolPath", "Ruta de pg_dump", defaultValue: "pg_dump"),
        ]);

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var format = settings.Get("format", "custom");
        var extension = format switch { "plain" => ".sql", "tar" => ".tar", _ => ".dump" };
        var output = Path.Combine(context.WorkingDirectory, context.BaseName + extension);

        var args = new List<string>(ConnectionArgs(settings))
        {
            "--format=" + format,
            "--file=" + output,
            "--no-password",
        };

        foreach (var schema in settings.GetList("schemas"))
        {
            args.Add("--schema=" + schema);
        }

        args.AddRange(SplitArgs(settings.Get("extraArgs")));

        context.Log.Info($"Ejecutando pg_dump ({format}) de {settings.Get("database")}@{settings.Get("host")}...");
        await processes.RunAsync(settings.Get("toolPath", "pg_dump"), args, environment: Environment(settings), cancellationToken: cancellationToken);
        return new BackupArtifact(output, extension);
    }

    public async Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        // pg_dump de solo el esquema de una tabla inexistente: valida credenciales sin volcar datos.
        var args = new List<string>(ConnectionArgs(settings)) { "--schema-only", "--table=__backup_connection_test__", "--no-password" };
        await processes.RunAsync(settings.Get("toolPath", "pg_dump"), args, environment: Environment(settings), cancellationToken: cancellationToken);
        return $"Conexión exitosa a {settings.Get("database")}@{settings.Get("host")}.";
    }

    private static IEnumerable<string> ConnectionArgs(ProviderSettings settings) =>
    [
        "--host=" + settings.Require("host"),
        "--port=" + settings.GetInt("port", 5432),
        "--username=" + settings.Require("user"),
        "--dbname=" + settings.Require("database"),
    ];

    private static Dictionary<string, string?> Environment(ProviderSettings settings) => new()
    {
        ["PGPASSWORD"] = settings.GetRaw("password"),
        ["PGSSLMODE"] = settings.Get("sslMode", "prefer"),
        ["PGCONNECT_TIMEOUT"] = "15",
    };

    internal static IEnumerable<string> SplitArgs(string? value) =>
        value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
