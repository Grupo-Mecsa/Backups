using Backup.Application.Providers;

namespace Backup.Providers.Databases;

/// <summary>
/// Servicios gestionados que usan las mismas herramientas que su motor (pg_dump, mysqldump, mongodump): solo cambian
/// los valores por defecto y las ayudas, para que configurarlos no requiera saber sus particularidades.
/// </summary>
public static class DatabaseVariants
{
    public static readonly ProviderVariant Supabase = new(
        "supabase",
        "Supabase",
        "Base PostgreSQL de un proyecto Supabase con pg_dump, por el pooler (compatible con IPv4).")
    {
        Customize = f => f.Key switch
        {
            "host" => f with
            {
                Placeholder = "aws-0-us-east-1.pooler.supabase.com",
                Help = "Panel de Supabase → Connect → Session pooler. La conexión directa (db.<ref>.supabase.co) solo funciona por IPv6.",
            },
            "port" => f with { Help = "5432 = Session pooler (el que necesita pg_dump). No uses 6543 (Transaction pooler)." },
            "user" => f with { DefaultValue = null, Placeholder = "postgres.abcdefghijklmnop", Help = "Con el pooler, el usuario lleva el ID del proyecto: postgres.<ref>." },
            "database" => f with { DefaultValue = "postgres" },
            "sslMode" => f with { DefaultValue = "require" },
            "schemas" => f with
            {
                DefaultValue = "public",
                Help = "Separados por coma. Los esquemas internos (auth, storage, realtime...) los administra Supabase y no se pueden restaurar en otra base; agrega solo los tuyos.",
            },
            _ => f,
        },
    };

    public static readonly ProviderVariant Neon = new(
        "neon",
        "Neon",
        "PostgreSQL serverless de Neon con pg_dump.")
    {
        Customize = f => f.Key switch
        {
            "host" => f with
            {
                Placeholder = "ep-cool-name-123456.us-east-2.aws.neon.tech",
                Help = "Consola de Neon → Connect. Usa el host sin «-pooler»: pg_dump necesita conexión directa.",
            },
            "user" => f with { DefaultValue = null, Placeholder = "neondb_owner" },
            "database" => f with { DefaultValue = "neondb" },
            "sslMode" => f with { DefaultValue = "require" },
            _ => f,
        },
    };

    public static readonly ProviderVariant AzurePostgreSql = new(
        "azurepostgres",
        "Azure Database for PostgreSQL",
        "Servidor flexible de Azure Database for PostgreSQL con pg_dump.")
    {
        Customize = f => f.Key switch
        {
            "host" => f with
            {
                Placeholder = "miservidor.postgres.database.azure.com",
                Help = "Permite la IP de este servidor en Redes del servidor flexible.",
            },
            "user" => f with { DefaultValue = null, Placeholder = "adminbackup" },
            "database" => f with { DefaultValue = "postgres" },
            "sslMode" => f with { DefaultValue = "require" },
            _ => f,
        },
    };

    public static readonly ProviderVariant AzureMySql = new(
        "azuremysql",
        "Azure Database for MySQL",
        "Servidor flexible de Azure Database for MySQL con mysqldump.")
    {
        Customize = f => f.Key switch
        {
            "host" => f with
            {
                Placeholder = "miservidor.mysql.database.azure.com",
                Help = "Permite la IP de este servidor en Redes del servidor flexible. Azure exige TLS, que el cliente usa por defecto.",
            },
            "user" => f with { DefaultValue = null, Placeholder = "adminbackup" },
            _ => f,
        },
    };

    public static readonly ProviderVariant MongoAtlas = new(
        "mongoatlas",
        "MongoDB Atlas",
        "Clúster de MongoDB Atlas con mongodump --archive.")
    {
        Customize = f => f.Key switch
        {
            "uri" => f with
            {
                Help = "Atlas → Connect → Drivers: mongodb+srv://usuario:clave@cluster0.abcde.mongodb.net/. Permite la IP de este servidor en Network Access.",
            },
            _ => f,
        },
    };
}
