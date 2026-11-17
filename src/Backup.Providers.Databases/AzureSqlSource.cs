using Backup.Application.Abstractions;
using Backup.Application.Providers;
using Microsoft.Data.SqlClient;

namespace Backup.Providers.Databases;

/// <summary>
/// Azure SQL Database (y cualquier SQL Server) exportado a .bacpac con SqlPackage: esquema y datos en un solo archivo,
/// sin carpeta compartida con el servidor. Es la vía para Azure SQL, que no admite <c>BACKUP DATABASE ... TO DISK</c>.
/// </summary>
public sealed class AzureSqlSource(IProcessRunner processes) : IBackupSource, IConnectionTester, IOptionLister, IRestoreTarget
{
    private static readonly string[] AuthenticationModes =
    [
        "SqlPassword",
        "ActiveDirectoryServicePrincipal",
        "ActiveDirectoryManagedIdentity",
        "ActiveDirectoryDefault",
    ];

    public ProviderDescriptor Descriptor { get; } = new(
        "azuresql",
        "Azure SQL Database",
        "Exporta la base a .bacpac con SqlPackage. Sirve también para SQL Server sin carpeta compartida.",
        ProviderCategory.Database,
        "database",
        [
            SettingField.Text("server", "Servidor", required: true, placeholder: "miservidor.database.windows.net",
                help: "Permite la IP de este servidor en el firewall del servidor lógico de Azure SQL.").ForConnection(),
            SettingField.Text("database", "Base de datos", required: true).Listable(),
            SettingField.Select("authentication", "Autenticación", AuthenticationModes, "SqlPassword",
                "SqlPassword: usuario y contraseña de SQL. ActiveDirectory*: cuentas de Microsoft Entra ID (service principal: usuario = Client ID, contraseña = secreto; identidad administrada: usuario = Client ID si es asignada por el usuario).").ForConnection(),
            SettingField.Text("user", "Usuario").ForConnection(),
            SettingField.Secret("password", "Contraseña").ForConnection(),
            SettingField.Toggle("trustServerCertificate", "Confiar en el certificado del servidor", false,
                "Solo para SQL Server propio con certificado autofirmado. En Azure SQL déjalo apagado.").ForConnection(),
            SettingField.Number("commandTimeout", "Tiempo máximo por comando (minutos)", 60),
            SettingField.Text("extraArgs", "Argumentos adicionales", placeholder: "/p:VerifyFullTextDocumentTypesSupported=false"),
            SettingField.Text("toolPath", "Ruta de sqlpackage", defaultValue: "sqlpackage").ForConnection(),
        ]);

    public string? ArtifactExtension(ProviderSettings settings) => ".bacpac";

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var output = Path.Combine(context.WorkingDirectory, context.BaseName + ".bacpac");
        var args = new List<string>
        {
            "/Action:Export",
            "/SourceConnectionString:" + BuildConnectionString(settings, settings.Require("database")),
            "/TargetFile:" + output,
            "/p:CommandTimeout=" + TimeoutSeconds(settings),
        };
        args.AddRange(PostgreSqlSource.SplitArgs(settings.Get("extraArgs")));

        context.Log.Info($"Exportando {settings.Get("database")}@{settings.Get("server")} a .bacpac con SqlPackage...");
        context.Log.Info("El .bacpac no es transaccionalmente consistente si la base recibe escrituras durante la exportación.");
        await processes.RunAsync(settings.Get("toolPath", "sqlpackage"), args, cancellationToken: cancellationToken);
        return new BackupArtifact(output, ".bacpac");
    }

    public async Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        // Directo a la base: en Azure SQL un usuario contenido no puede entrar a master.
        await using var connection = new SqlConnection(BuildConnectionString(settings, settings.Require("database")));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), DB_NAME()";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return $"Conexión exitosa a {reader.GetString(1)} ({reader.GetString(0)}).";
    }

    public async Task<IReadOnlyList<string>> ListOptionsAsync(string fieldKey, ProviderSettings settings, CancellationToken cancellationToken)
    {
        if (fieldKey != "database")
        {
            throw new NotSupportedException($"El campo '{fieldKey}' no se puede listar.");
        }

        await using var connection = new SqlConnection(BuildConnectionString(settings, "master"));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 AND HAS_DBACCESS(name) = 1 ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public bool CanRestore(string artifactFileName) => artifactFileName.EndsWith(".bacpac", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SettingField> RestoreFields =>
    [
        .. Descriptor.Fields.Where(f => f.IsConnection || f.Key is "database" or "commandTimeout"),
        SettingField.Toggle("recreateDatabase", "Borrar la base si existe", false,
            "SqlPackage solo importa en una base nueva o vacía. Activado: elimina la base destino con TODO su contenido antes de importar."),
        SettingField.Text("edition", "Edición en Azure (opcional)", placeholder: "GeneralPurpose",
            help: "Basic, Standard, Premium, GeneralPurpose, BusinessCritical o Hyperscale. Vacío = la predeterminada del servidor."),
        SettingField.Text("serviceObjective", "Objetivo de servicio (opcional)", placeholder: "GP_S_Gen5_2",
            help: "Tamaño de la base nueva en Azure, p. ej. S0, P1 o GP_S_Gen5_2."),
    ];

    public string DescribeTarget(ProviderSettings settings) => $"{settings.Get("database")} @ {settings.Get("server")}";

    public async Task<bool> RestoreAsync(RestoreContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var database = settings.Require("database");
        if (settings.GetBool("recreateDatabase"))
        {
            if (database.ToLowerInvariant() is "master" or "model" or "msdb" or "tempdb")
            {
                throw new InvalidOperationException($"No se borra la base de sistema «{database}». Restaura en otra base.");
            }

            context.Log.Warn($"Borrando la base {database} para importar el .bacpac en una base nueva...");
            await using var connection = new SqlConnection(BuildConnectionString(settings, "master"));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS [{database.Replace("]", "]]", StringComparison.Ordinal)}]";
            command.CommandTimeout = TimeoutSeconds(settings);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var args = new List<string>
        {
            "/Action:Import",
            "/TargetConnectionString:" + BuildConnectionString(settings, database),
            "/SourceFile:" + context.ArtifactFile,
            "/p:CommandTimeout=" + TimeoutSeconds(settings),
        };
        if (settings.Get("edition") is { } edition)
        {
            args.Add("/p:DatabaseEdition=" + edition);
        }

        if (settings.Get("serviceObjective") is { } objective)
        {
            args.Add("/p:DatabaseServiceObjective=" + objective);
        }

        context.Log.Info($"Importando el .bacpac en {DescribeTarget(settings)} con SqlPackage...");
        await processes.RunAsync(settings.Get("toolPath", "sqlpackage"), args, cancellationToken: cancellationToken);
        return true;
    }

    private static int TimeoutSeconds(ProviderSettings settings) => Math.Max(1, settings.GetInt("commandTimeout", 60)) * 60;

    internal static string BuildConnectionString(ProviderSettings settings, string database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Require("server"),
            InitialCatalog = database,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = settings.GetBool("trustServerCertificate"),
            ApplicationName = "Backup",
            ConnectTimeout = 30,
        };

        var mode = settings.Get("authentication", "SqlPassword");
        if (!AuthenticationModes.Contains(mode, StringComparer.OrdinalIgnoreCase)
            || !Enum.TryParse<SqlAuthenticationMethod>(mode, ignoreCase: true, out var authentication))
        {
            throw new InvalidOperationException($"Autenticación desconocida: '{mode}'.");
        }

        if (authentication != SqlAuthenticationMethod.SqlPassword)
        {
            builder.Authentication = authentication;
        }

        if (settings.Get("user") is { } user)
        {
            builder.UserID = user;
        }
        else if (authentication is SqlAuthenticationMethod.SqlPassword or SqlAuthenticationMethod.ActiveDirectoryServicePrincipal)
        {
            throw new InvalidOperationException("Indica el usuario.");
        }

        if (settings.GetRaw("password") is { } password)
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }
}
