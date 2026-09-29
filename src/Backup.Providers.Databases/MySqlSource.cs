using Backup.Application.Abstractions;
using Backup.Application.Providers;

namespace Backup.Providers.Databases;

/// <summary>Respaldo de MySQL / MariaDB con mysqldump (o mariadb-dump).</summary>
public sealed class MySqlSource(IProcessRunner processes) : IBackupSource, IConnectionTester
{
    public ProviderDescriptor Descriptor { get; } = new(
        "mysql",
        "MySQL / MariaDB",
        "Dump SQL con mysqldump, consistente con --single-transaction.",
        ProviderCategory.Database,
        "database",
        [
            SettingField.Text("host", "Host", required: true),
            SettingField.Number("port", "Puerto", 3306),
            SettingField.Text("databases", "Bases de datos", placeholder: "tienda, crm", help: "Separadas por coma. Vacío = todas."),
            SettingField.Text("user", "Usuario", required: true, defaultValue: "root"),
            SettingField.Secret("password", "Contraseña"),
            SettingField.Toggle("singleTransaction", "--single-transaction", true, "Respaldo consistente sin bloquear tablas InnoDB."),
            SettingField.Toggle("routines", "Incluir rutinas, triggers y eventos", true),
            SettingField.Text("extraArgs", "Argumentos adicionales", placeholder: "--skip-ssl"),
            SettingField.Text("toolPath", "Ruta de mysqldump", defaultValue: "mysqldump"),
        ]);

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var output = Path.Combine(context.WorkingDirectory, context.BaseName + ".sql");
        var args = new List<string>(ConnectionArgs(settings)) { "--result-file=" + output };

        if (settings.GetBool("singleTransaction", true))
        {
            args.Add("--single-transaction");
            args.Add("--quick");
        }

        if (settings.GetBool("routines", true))
        {
            args.AddRange(["--routines", "--triggers", "--events"]);
        }

        args.AddRange(PostgreSqlSource.SplitArgs(settings.Get("extraArgs")));

        var databases = settings.GetList("databases");
        if (databases.Count == 0)
        {
            args.Add("--all-databases");
        }
        else
        {
            args.Add("--databases");
            args.AddRange(databases);
        }

        context.Log.Info($"Ejecutando mysqldump en {settings.Get("host")} ({(databases.Count == 0 ? "todas las bases" : string.Join(", ", databases))})...");
        await processes.RunAsync(settings.Get("toolPath", "mysqldump"), args, environment: Environment(settings), cancellationToken: cancellationToken);
        return new BackupArtifact(output, ".sql");
    }

    public async Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var args = new List<string>(ConnectionArgs(settings)) { "--no-data", "--databases", "mysql", "--tables", "__backup_connection_test__" };
        args.AddRange(PostgreSqlSource.SplitArgs(settings.Get("extraArgs")));
        try
        {
            await processes.RunAsync(settings.Get("toolPath", "mysqldump"), args, environment: Environment(settings), cancellationToken: cancellationToken);
        }
        catch (ProcessFailedException ex) when (ex.Message.Contains("__backup_connection_test__", StringComparison.Ordinal))
        {
            // La tabla no existe, pero la autenticación fue correcta.
        }

        return $"Conexión exitosa a {settings.Get("host")}.";
    }

    private static IEnumerable<string> ConnectionArgs(ProviderSettings settings) =>
    [
        "--host=" + settings.Require("host"),
        "--port=" + settings.GetInt("port", 3306),
        "--user=" + settings.Require("user"),
        "--connect-timeout=15",
    ];

    private static Dictionary<string, string?> Environment(ProviderSettings settings) =>
        new() { ["MYSQL_PWD"] = settings.GetRaw("password") };
}
