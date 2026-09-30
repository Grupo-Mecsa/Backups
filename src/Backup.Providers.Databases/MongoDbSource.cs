using Backup.Application.Abstractions;
using Backup.Application.Providers;

namespace Backup.Providers.Databases;

/// <summary>Respaldo de MongoDB con mongodump en formato archive.</summary>
public sealed class MongoDbSource(IProcessRunner processes) : IBackupSource
{
    public ProviderDescriptor Descriptor { get; } = new(
        "mongodb",
        "MongoDB",
        "Archivo único con mongodump --archive (restaurable con mongorestore).",
        ProviderCategory.Database,
        "database",
        [
            new SettingField("uri", "URI de conexión", SettingFieldType.Password)
            {
                Required = true,
                Help = "mongodb://usuario:clave@host:27017/?authSource=admin o mongodb+srv://...",
            }.ForConnection(),
            SettingField.Text("database", "Base de datos", help: "Vacío = todas."),
            SettingField.Toggle("oplog", "Incluir oplog (--oplog)", false, "Solo para replica sets y respaldo de todas las bases."),
            SettingField.Text("extraArgs", "Argumentos adicionales"),
            SettingField.Text("toolPath", "Ruta de mongodump", defaultValue: "mongodump").ForConnection(),
        ]);

    public async Task<BackupArtifact> CreateArtifactAsync(SourceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var output = Path.Combine(context.WorkingDirectory, context.BaseName + ".archive");
        var args = new List<string> { "--uri=" + settings.Require("uri"), "--archive=" + output };

        if (settings.Get("database") is { } database)
        {
            args.Add("--db=" + database);
        }

        if (settings.GetBool("oplog"))
        {
            args.Add("--oplog");
        }

        args.AddRange(PostgreSqlSource.SplitArgs(settings.Get("extraArgs")));

        context.Log.Info("Ejecutando mongodump...");
        await processes.RunAsync(settings.Get("toolPath", "mongodump"), args, cancellationToken: cancellationToken);
        return new BackupArtifact(output, ".archive");
    }
}
