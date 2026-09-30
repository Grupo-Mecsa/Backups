using System.Text.RegularExpressions;
using Backup.Application.Abstractions;
using Backup.Application.Providers;

namespace Backup.Providers.Databases;

/// <summary>Respaldo de PostgreSQL con pg_dump (la versión de pg_dump debe ser ≥ la del servidor).</summary>
public sealed partial class PostgreSqlSource(IProcessRunner processes) : IBackupSource, IConnectionTester, IOptionLister, IRestoreTarget
{
    public ProviderDescriptor Descriptor { get; } = new(
        "postgres",
        "PostgreSQL",
        "Dump lógico con pg_dump. Compatible con servidores propios y servicios gestionados (RDS, Azure Database, Cloud SQL...).",
        ProviderCategory.Database,
        "database",
        [
            SettingField.Text("host", "Host", required: true, placeholder: "db.example.com").ForConnection(),
            SettingField.Number("port", "Puerto", 5432).ForConnection(),
            SettingField.Text("database", "Base de datos", required: true).Listable(),
            SettingField.Text("user", "Usuario", required: true, defaultValue: "postgres").ForConnection(),
            SettingField.Secret("password", "Contraseña").ForConnection(),
            SettingField.Select("format", "Formato", ["custom", "plain", "tar"], "custom",
                "custom: restaurable con pg_restore y ya comprimido. plain: script SQL."),
            SettingField.Select("sslMode", "SSL", ["prefer", "require", "disable", "verify-full"], "prefer").ForConnection(),
            SettingField.Text("schemas", "Esquemas (opcional)", placeholder: "public, ventas", help: "Separados por coma. Vacío = todos.").Listable(multiple: true),
            SettingField.Text("extraArgs", "Argumentos adicionales", placeholder: "--no-owner --no-privileges"),
            SettingField.Text("toolPath", "Ruta de pg_dump", defaultValue: "pg_dump").ForConnection(),
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

        var schemas = settings.GetList("schemas");
        foreach (var schema in schemas)
        {
            args.Add("--schema=" + SchemaPattern(schema));
        }

        args.AddRange(SplitArgs(settings.Get("extraArgs")));

        context.Log.Info($"Ejecutando pg_dump ({format}) de {settings.Get("database")}@{settings.Get("host")}...");
        context.Log.Info(schemas.Count == 0 ? "Esquemas: todos." : $"Esquemas: {string.Join(", ", schemas)}.");
        try
        {
            await processes.RunAsync(settings.Get("toolPath", "pg_dump"), args, environment: Environment(settings), cancellationToken: cancellationToken);
        }
        catch (ProcessFailedException ex) when (ex.Message.Contains("row-level security", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProcessFailedException(ex.Message + "\n\n" + RowSecurityHint(settings.Get("user")));
        }
        catch (ProcessFailedException ex) when (ex.Message.Contains("permission denied for", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProcessFailedException(ex.Message + "\n\n" + GrantsHint(settings.Get("user"), schemas));
        }
        catch (ProcessFailedException ex) when (ex.Message.Contains("no matching schemas", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProcessFailedException(ex.Message + "\n\n" + await MissingSchemasHintAsync(settings, schemas, cancellationToken));
        }

        return new BackupArtifact(output, extension);
    }

    public string? ArtifactExtension(ProviderSettings settings) =>
        settings.Get("format", "custom") switch { "plain" => ".sql", "tar" => ".tar", _ => ".dump" };

    public bool CanRestore(string artifactFileName) =>
        artifactFileName.EndsWith(".dump", StringComparison.OrdinalIgnoreCase)
        || artifactFileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
        || artifactFileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SettingField> RestoreFields =>
    [
        .. Descriptor.Fields.Where(f => f.IsConnection || f.Key == "database"),
        SettingField.Toggle("createDatabase", "Crear la base si no existe", true),
        SettingField.Toggle("recreateDatabase", "Borrar la base completa y crearla de nuevo", false,
            "Para copias de prueba: elimina la base destino con TODO su contenido (también lo que no está en el respaldo) y la crea vacía antes de restaurar. Nunca se aplica a la base «postgres»."),
        SettingField.Toggle(PlatformCompatKey, "Compatibilidad con PostgreSQL gestionado", true,
            "Antes de restaurar, crea lo que algunas plataformas PostgreSQL gestionadas traen de fábrica y sus respaldos dan por hecho: el esquema extensions (pgcrypto, uuid-ossp), los roles anon, authenticated y service_role, y auth.uid(), auth.role() y auth.jwt() si la base no tiene esquema auth. Sin esto, las tablas que usan extensions.gen_random_uuid() no se crean."),
        SettingField.Toggle("clean", "Borrar antes los objetos existentes (--clean)", false,
            "Elimina las tablas, vistas y funciones del respaldo que ya existan en la base, con sus datos, y las crea de nuevo. Úsalo para reemplazar una copia anterior."),
        SettingField.Toggle("noOwner", "Sin dueños ni privilegios (--no-owner --no-privileges)", true,
            "Recomendado al restaurar en otro servidor (p. ej. un respaldo de un servicio gestionado en un PostgreSQL propio): evita errores por roles que no existen."),
    ];

    public string DescribeTarget(ProviderSettings settings) =>
        $"{settings.Get("database")} @ {settings.Get("host")}:{settings.GetInt("port", 5432)}";

    public async Task<bool> RestoreAsync(RestoreContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var database = settings.Require("database");
        var psql = ClientTools.Sibling(settings.Get("toolPath"), "pg_dump", "psql");

        var quotedDatabase = "\"" + database.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        if (settings.GetBool("recreateDatabase"))
        {
            if (string.Equals(database, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("No se borra la base «postgres» (es la de mantenimiento del servidor). Restaura en otra base.");
            }

            context.Log.Warn($"Borrando la base {database} con todo su contenido para crearla de nuevo...");
            await processes.RunAsync(psql, [.. ClientArgs(settings, "postgres"), $"--command=DROP DATABASE IF EXISTS {quotedDatabase} WITH (FORCE)"],
                environment: Environment(settings), cancellationToken: cancellationToken);
            await processes.RunAsync(psql, [.. ClientArgs(settings, "postgres"), $"--command=CREATE DATABASE {quotedDatabase}"],
                environment: Environment(settings), cancellationToken: cancellationToken);
        }
        else if (settings.GetBool("createDatabase", true))
        {
            var exists = await ClientTools.ReadLinesAsync(processes, psql,
                [.. ClientArgs(settings, "postgres"), "--tuples-only", "--no-align",
                    $"--command=SELECT 1 FROM pg_database WHERE datname = '{database.Replace("'", "''", StringComparison.Ordinal)}'"],
                Environment(settings), cancellationToken);
            if (exists.Count == 0)
            {
                context.Log.Info($"Creando la base {database}...");
                await processes.RunAsync(psql,
                    [.. ClientArgs(settings, "postgres"), $"--command=CREATE DATABASE \"{database.Replace("\"", "\"\"", StringComparison.Ordinal)}\""],
                    environment: Environment(settings), cancellationToken: cancellationToken);
            }
        }

        // "supabaseCompat" es el nombre anterior de la opción: se sigue leyendo para no cambiar trabajos ya guardados.
        if (settings.GetBool(PlatformCompatKey, settings.GetBool("supabaseCompat", true)))
        {
            context.Log.Info("Preparando compatibilidad con PostgreSQL gestionado (esquema extensions, roles y funciones auth)...");
            var script = Path.Combine(context.WorkingDirectory, "platform-compat.sql");
            await File.WriteAllTextAsync(script, PlatformCompatScript, cancellationToken);
            await processes.RunAsync(psql, [.. ClientArgs(settings, database), "--file=" + script],
                environment: Environment(settings), cancellationToken: cancellationToken);
        }

        if (context.ArtifactFile.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            // Script plano: psql sigue ante errores de sentencias sueltas (como pg_restore), así no se corta a medias.
            context.Log.Info("Ejecutando el script SQL con psql...");
            await processes.RunAsync(psql, [.. ClientArgs(settings, database), "--file=" + context.ArtifactFile],
                environment: Environment(settings), cancellationToken: cancellationToken);
            context.Log.Warn("Con un script .sql no se pueden contar los errores de sentencias sueltas: revisa la base restaurada.");
            return true;
        }

        var args = new List<string>(ConnectionArgs(settings).Where(a => !a.StartsWith("--dbname=", StringComparison.Ordinal)))
        {
            "--dbname=" + database,
            "--no-password",
        };
        if (settings.GetBool("clean"))
        {
            args.AddRange(["--clean", "--if-exists"]);
        }

        if (settings.GetBool("noOwner", true))
        {
            args.AddRange(["--no-owner", "--no-privileges"]);
        }

        args.Add(context.ArtifactFile);
        context.Log.Info($"Ejecutando pg_restore en {database}...");
        try
        {
            await processes.RunAsync(ClientTools.Sibling(settings.Get("toolPath"), "pg_dump", "pg_restore"), args,
                environment: Environment(settings), cancellationToken: cancellationToken);
            return true;
        }
        catch (ProcessFailedException ex) when (ex.Message.Contains("errors ignored on restore", StringComparison.OrdinalIgnoreCase)
                                                 || ex.Message.Contains("could not execute query", StringComparison.OrdinalIgnoreCase))
        {
            // pg_restore sigue ante errores y los resume al final (p. ej. roles o extensiones que no existen en el destino).
            context.Log.Warn(ex.Message.Length > 6000 ? ex.Message[..6000] + " [...]" : ex.Message);
            return false;
        }
    }

    private const string PlatformCompatKey = "platformCompat";

    /// <summary>
    /// Lo que los respaldos de algunas plataformas PostgreSQL gestionadas dan por hecho que existe. Todo es
    /// idempotente, y el esquema auth solo se simula si la base no lo tiene (si ya existe, no se toca). Lo que falle
    /// (p. ej. crear roles sin ser superusuario) no detiene la restauración.
    /// </summary>
    private const string PlatformCompatScript = """
        CREATE SCHEMA IF NOT EXISTS extensions;
        CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;
        CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA extensions;

        DO $$
        BEGIN
          IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'anon') THEN CREATE ROLE anon NOLOGIN NOINHERIT; END IF;
          IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'authenticated') THEN CREATE ROLE authenticated NOLOGIN NOINHERIT; END IF;
          IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_role') THEN CREATE ROLE service_role NOLOGIN NOINHERIT; END IF;
        END $$;

        DO $$
        BEGIN
          IF NOT EXISTS (SELECT FROM pg_namespace WHERE nspname = 'auth') THEN
            CREATE SCHEMA auth;
            CREATE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE
              AS $f$ SELECT nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $f$;
            CREATE FUNCTION auth.role() RETURNS text LANGUAGE sql STABLE
              AS $f$ SELECT nullif(current_setting('request.jwt.claim.role', true), '')::text $f$;
            CREATE FUNCTION auth.jwt() RETURNS jsonb LANGUAGE sql STABLE
              AS $f$ SELECT coalesce(nullif(current_setting('request.jwt.claims', true), ''), '{}')::jsonb $f$;
          END IF;
        END $$;
        """;

    /// <summary>Argumentos de psql contra <paramref name="database"/> (sin psqlrc ni pedir contraseña).</summary>
    private static string[] ClientArgs(ProviderSettings settings, string database) =>
    [
        "--host=" + settings.Require("host"),
        "--port=" + settings.GetInt("port", 5432),
        "--username=" + settings.Require("user"),
        "--dbname=" + database,
        "--no-password",
        "--no-psqlrc",
    ];

    public async Task<string> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        // pg_dump de solo el esquema de una tabla inexistente: valida credenciales sin volcar datos.
        var args = new List<string>(ConnectionArgs(settings)) { "--schema-only", "--table=__backup_connection_test__", "--no-password" };
        await processes.RunAsync(settings.Get("toolPath", "pg_dump"), args, environment: Environment(settings), cancellationToken: cancellationToken);
        return $"Conexión exitosa a {settings.Get("database")}@{settings.Get("host")}.";
    }

    public Task<IReadOnlyList<string>> ListOptionsAsync(string fieldKey, ProviderSettings settings, CancellationToken cancellationToken)
    {
        // Solo lo que el usuario puede leer: con un usuario de solo lectura, un esquema sin permiso haría fallar pg_dump.
        var (database, query) = fieldKey switch
        {
            "database" => (settings.Get("database") ?? "postgres",
                "select datname from pg_database where datallowconn and not datistemplate and has_database_privilege(datname, 'CONNECT') order by 1"),
            "schemas" => (settings.Get("database") ?? throw new InvalidOperationException("Indica primero la base de datos."),
                "select nspname from pg_namespace where nspname !~ '^pg_' and nspname <> 'information_schema' and has_schema_privilege(oid, 'USAGE') order by 1"),
            _ => throw new NotSupportedException($"El campo '{fieldKey}' no se puede listar."),
        };

        string[] args =
        [
            "--host=" + settings.Require("host"),
            "--port=" + settings.GetInt("port", 5432),
            "--username=" + settings.Require("user"),
            "--dbname=" + database,
            "--no-password",
            "--no-psqlrc",
            "--tuples-only",
            "--no-align",
            "--command=" + query,
        ];
        return ClientTools.ReadLinesAsync(
            processes, ClientTools.Sibling(settings.Get("toolPath"), "pg_dump", "psql"), args, Environment(settings), cancellationToken);
    }

    /// <summary>
    /// pg_dump trata cada --schema como patrón y pasa a minúsculas lo que no va entre comillas: "Proyectos" buscaría
    /// "proyectos". Los nombres que no son identificadores simples se entrecomillan para que coincidan tal cual; los
    /// que traen comodines (* ?) o comillas se respetan como patrón escrito a mano.
    /// </summary>
    internal static string SchemaPattern(string schema) =>
        schema.IndexOfAny(['*', '?', '"']) >= 0 || SimpleIdentifier().IsMatch(schema)
            ? schema
            : "\"" + schema + "\"";

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex SimpleIdentifier();

    /// <summary>Permisos de lectura que necesita un usuario de solo lectura para respaldar los esquemas elegidos.</summary>
    private static string GrantsHint(string? user, IReadOnlyList<string> schemas)
    {
        var role = Role(user);
        var lines = (schemas.Count == 0 ? ["esquema"] : schemas).Select(schema =>
        {
            var quoted = "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            return $"""
                  GRANT USAGE ON SCHEMA {quoted} TO {role};
                  GRANT SELECT ON ALL TABLES IN SCHEMA {quoted} TO {role};
                  GRANT SELECT ON ALL SEQUENCES IN SCHEMA {quoted} TO {role};
                  ALTER DEFAULT PRIVILEGES IN SCHEMA {quoted} GRANT SELECT ON TABLES TO {role};
                  ALTER DEFAULT PRIVILEGES IN SCHEMA {quoted} GRANT SELECT ON SEQUENCES TO {role};
                """;
        });
        return $"El usuario '{role}' no puede leer algún objeto (tabla o secuencia). Ejecuta como propietario de los objetos (o un administrador):\n"
            + string.Join("\n", lines);
    }

    /// <summary>Rol sin el sufijo ".identificador" que exigen algunos poolers de conexiones (usuario.proyecto).</summary>
    private static string Role(string? user) => string.IsNullOrWhiteSpace(user) ? "tu_usuario" : user.Split('.')[0];

    /// <summary>Para un "no matching schemas": qué esquemas sí puede usar el usuario, para comparar con lo configurado.</summary>
    private async Task<string> MissingSchemasHintAsync(ProviderSettings settings, IReadOnlyList<string> requested, CancellationToken cancellationToken)
    {
        try
        {
            var available = await ListOptionsAsync("schemas", settings, cancellationToken);
            return $"""
                Ninguno de los esquemas pedidos ({string.Join(", ", requested)}) existe en la base '{settings.Get("database")}'.
                Esquemas disponibles para este usuario: {(available.Count == 0 ? "ninguno" : string.Join(", ", available))}.
                Usa el botón «Elegir» del campo Esquemas para marcarlos, o déjalo vacío para respaldar todos.
                """;
        }
        catch (Exception ex) when (ex is ProcessFailedException or InvalidOperationException or IOException)
        {
            return $"Revisa el campo Esquemas: ninguno de ({string.Join(", ", requested)}) existe en la base '{settings.Get("database")}'.";
        }
    }

    /// <summary>
    /// pg_dump se niega a copiar tablas con RLS si el usuario no puede saltarse las políticas (así no deja un respaldo
    /// incompleto sin avisar). Es lo habitual en servicios gestionados con un usuario de solo lectura.
    /// </summary>
    private static string RowSecurityHint(string? user)
    {
        var role = Role(user);
        return $"""
            Hay tablas con seguridad por fila (RLS) y el usuario '{role}' no puede saltarse sus políticas. Soluciones:
              1. Recomendada: ALTER ROLE {role} BYPASSRLS;
              2. Si no está permitido: crear en cada tabla con RLS una política de solo lectura para ese usuario
                 (CREATE POLICY respaldo_lectura ON esquema.tabla FOR SELECT TO {role} USING (true);).
              3. Respaldar con un usuario que ya tenga BYPASSRLS (p. ej. el propietario de las tablas).
            No uses --enable-row-security: respaldaría solo las filas visibles para ese usuario, sin avisar.
            """;
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
