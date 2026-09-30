using Backup.Application.Abstractions;
using Backup.Application.Providers;

namespace Backup.Providers.Databases;

/// <summary>Respaldo de MySQL / MariaDB con mysqldump (o mariadb-dump).</summary>
public sealed class MySqlSource(IProcessRunner processes) : IBackupSource, IConnectionTester, IOptionLister, IRestoreTarget
{
    public ProviderDescriptor Descriptor { get; } = new(
        "mysql",
        "MySQL / MariaDB",
        "Dump SQL con mysqldump, consistente con --single-transaction.",
        ProviderCategory.Database,
        "database",
        [
            SettingField.Text("host", "Host", required: true).ForConnection(),
            SettingField.Number("port", "Puerto", 3306).ForConnection(),
            SettingField.Text("databases", "Bases de datos", placeholder: "tienda, crm", help: "Separadas por coma. Vacío = todas.").Listable(multiple: true),
            SettingField.Text("user", "Usuario", required: true, defaultValue: "root").ForConnection(),
            SettingField.Secret("password", "Contraseña").ForConnection(),
            SettingField.Toggle("singleTransaction", "--single-transaction", true, "Respaldo consistente sin bloquear tablas InnoDB."),
            SettingField.Toggle("routines", "Incluir rutinas, triggers y eventos", true),
            SettingField.Text("extraArgs", "Argumentos adicionales", placeholder: "--skip-ssl"),
            SettingField.Text("toolPath", "Ruta de mysqldump", defaultValue: "mysqldump").ForConnection(),
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

    public string? ArtifactExtension(ProviderSettings settings) => ".sql";

    public bool CanRestore(string artifactFileName) => artifactFileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    /// <summary>Solo el servidor: el volcado se hizo con --databases, así que trae sus CREATE DATABASE y USE.</summary>
    public IReadOnlyList<SettingField> RestoreFields => [.. Descriptor.Fields.Where(f => f.IsConnection)];

    public string DescribeTarget(ProviderSettings settings) => $"{settings.Get("host")}:{settings.GetInt("port", 3306)}";

    public async Task<bool> RestoreAsync(RestoreContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var args = new List<string>(ConnectionArgs(settings)) { "--execute=source " + context.ArtifactFile };
        args.AddRange(PostgreSqlSource.SplitArgs(settings.Get("extraArgs"))
            .Where(a => a.StartsWith("--ssl", StringComparison.Ordinal) || a.StartsWith("--skip-ssl", StringComparison.Ordinal)));
        context.Log.Info("Ejecutando el script con mysql (crea las bases que indica el propio respaldo)...");
        await processes.RunAsync(ClientTools.Sibling(settings.Get("toolPath"), "mysqldump", "mysql"), args,
            environment: Environment(settings), cancellationToken: cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<string>> ListOptionsAsync(string fieldKey, ProviderSettings settings, CancellationToken cancellationToken)
    {
        if (fieldKey != "databases")
        {
            throw new NotSupportedException($"El campo '{fieldKey}' no se puede listar.");
        }

        // De los argumentos adicionales solo los de SSL: los demás son de mysqldump y el cliente mysql no los acepta.
        var args = new List<string>(ConnectionArgs(settings)) { "--batch", "--skip-column-names", "--execute=SHOW DATABASES" };
        args.AddRange(PostgreSqlSource.SplitArgs(settings.Get("extraArgs"))
            .Where(a => a.StartsWith("--ssl", StringComparison.Ordinal) || a.StartsWith("--skip-ssl", StringComparison.Ordinal)));
        var databases = await ClientTools.ReadLinesAsync(
            processes, ClientTools.Sibling(settings.Get("toolPath"), "mysqldump", "mysql"), args, Environment(settings), cancellationToken);
        return [.. databases.Where(d => d is not ("information_schema" or "performance_schema" or "sys"))];
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
