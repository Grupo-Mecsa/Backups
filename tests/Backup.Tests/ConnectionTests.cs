using Backup.Application.Abstractions;
using Backup.Application.Connections;
using Backup.Application.Jobs;
using Backup.Application.Providers;
using Backup.Application.Runs;
using Backup.Application.Security;
using Backup.Application.Telegram;
using Backup.Domain.Connections;
using Backup.Domain.Jobs;
using Backup.Domain.Runs;
using Backup.Infrastructure;
using Backup.Infrastructure.Persistence;
using Backup.Providers.Cloud;
using Backup.Providers.Databases;
using Backup.Providers.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Tests;

/// <summary>Banco de conexiones con el cableado real (SQLite, Data Protection, proveedores).</summary>
public sealed class ConnectionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "backup-conn-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = default!;
    private readonly TestUser _user = new();

    private ConnectionService Connections => _services.GetRequiredService<ConnectionService>();
    private JobService Jobs => _services.GetRequiredService<JobService>();

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:WorkingDirectory"] = Path.Combine(_root, "work") })
            .Build();

        _services = new ServiceCollection()
            .AddLogging()
            .AddBackupInfrastructure(configuration, Path.Combine(_root, "data"))
            .AddDatabaseProviders()
            .AddCloudProviders()
            .AddFileProviders()
            .AddScoped<ICurrentUser>(_ => _user)
            .AddSingleton<IEmailSender>(new FakeEmailSender())
            .AddSingleton<ITelegramBot>(new FakeTelegramBot())
            .AddSingleton<IAccountLinks, TestLinks>()
            .BuildServiceProvider();

        await _services.MigrateBackupDatabaseAsync();
        _user.TenantId = (await _services.GetRequiredService<ITenantRepository>().ListAsync()).Single().Id;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Job_UsingConnection_KeepsOnlyItsOwnFields_AndResolvesAccessFromConnection()
    {
        var connectionId = await CreateFtpConnectionAsync("NAS oficina", "secreto");

        var source = Binding("ftp", ("remotePath", "/datos"), ("host", "otro.host"), ("password", "no-guardar"));
        source.ConnectionId = connectionId;
        var save = await Jobs.SaveAsync(new BackupJob { Name = "Documentos", Source = source, Destination = Binding("local", ("path", _root)) });
        Assert.True(save.Success, string.Join("; ", save.Errors.Select(e => e.Message)));

        // El trabajo guarda solo lo propio; los datos de acceso quedan en la conexión.
        var stored = await _services.GetRequiredService<IJobRepository>().GetAsync(save.JobId);
        Assert.Equal(connectionId, stored!.Source.ConnectionId);
        Assert.Equal("/datos", stored.Source.Settings["remotePath"]);
        Assert.False(stored.Source.Settings.ContainsKey("host"));
        Assert.False(stored.Source.Settings.ContainsKey("password"));

        var resolved = await _services.GetRequiredService<ConnectionResolver>().ResolveAsync(stored);
        Assert.Equal("nas.local", resolved.Source.Settings["host"]);
        Assert.Equal("secreto", resolved.Source.Settings["password"]);
        Assert.Equal("/datos", resolved.Source.Settings["remotePath"]);

        // En la base, la contraseña de la conexión está cifrada.
        await using (var db = await _services.GetRequiredService<IDbContextFactory<BackupDbContext>>().CreateDbContextAsync())
        {
            var json = (await db.Connections.SingleAsync()).SettingsJson;
            Assert.DoesNotContain("secreto", json, StringComparison.Ordinal);
        }

        // En uso: no se puede eliminar, y la lista lo indica.
        Assert.NotNull(await Connections.DeleteAsync(connectionId));
        Assert.Equal(1, (await Connections.ListAsync()).Single().JobCount);
    }

    [Fact]
    public async Task EditingConnection_WithEmptySecret_KeepsStoredSecret_AndNeverReturnsIt()
    {
        var connectionId = await CreateFtpConnectionAsync("NAS", "secreto");

        var draft = await Connections.GetForEditAsync(connectionId);
        Assert.NotNull(draft);
        Assert.True(draft.HasStoredSecret("password"));
        Assert.False(draft.Connection.Settings.ContainsKey("password"));

        draft.Connection.Settings["host"] = "nas2.local";
        Assert.True((await Connections.SaveAsync(draft.Connection)).Success);

        var updated = await _services.GetRequiredService<IConnectionRepository>().GetAsync(connectionId);
        Assert.Equal("nas2.local", updated!.Settings["host"]);
        Assert.Equal("secreto", updated.Settings["password"]);
    }

    [Fact]
    public async Task SaveAsConnection_FromExistingJob_ReusesItsStoredPassword()
    {
        var save = await Jobs.SaveAsync(new BackupJob
        {
            Name = "Ftp directo",
            Source = Binding("ftp", ("host", "nas.local"), ("user", "backup"), ("password", "guardada"), ("remotePath", "/datos")),
            Destination = Binding("local", ("path", _root)),
        });
        Assert.True(save.Success, string.Join("; ", save.Errors.Select(e => e.Message)));

        // En el asistente la contraseña llega vacía ("sin cambios").
        var fromUi = Binding("ftp", ("host", "nas.local"), ("user", "backup"), ("password", string.Empty), ("remotePath", "/datos"));
        var result = await Jobs.SaveAsConnectionAsync(ProviderRole.Source, fromUi, save.JobId, "Desde trabajo");
        Assert.True(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));

        var connection = await _services.GetRequiredService<IConnectionRepository>().GetAsync(result.ConnectionId);
        Assert.Equal("guardada", connection!.Settings["password"]);
        Assert.False(connection.Settings.ContainsKey("remotePath"));
    }

    [Fact]
    public async Task Run_FailsClearly_WhenConnectionWasRemoved()
    {
        var connectionId = await CreateFtpConnectionAsync("Temporal", "x");
        var source = Binding("ftp", ("remotePath", "/datos"));
        source.ConnectionId = connectionId;
        var save = await Jobs.SaveAsync(new BackupJob { Name = "Huerfano", Source = source, Destination = Binding("local", ("path", _root)) });
        Assert.True(save.Success, string.Join("; ", save.Errors.Select(e => e.Message)));

        await _services.GetRequiredService<IConnectionRepository>().DeleteAsync(connectionId);

        var run = await _services.GetRequiredService<IBackupRunner>().RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("conexión", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_CopiesEverything_UnderNewPausedName()
    {
        var connectionId = await CreateFtpConnectionAsync("NAS", "secreto");
        var source = Binding("ftp", ("remotePath", "/datos"));
        source.ConnectionId = connectionId;
        var save = await Jobs.SaveAsync(new BackupJob
        {
            Name = "Documentos",
            Source = source,
            Destination = Binding("local", ("path", _root)),
            EncryptionPassphrase = "clave-segura",
            Schedule = "0 3 * * 0",
        });
        Assert.True(save.Success, string.Join("; ", save.Errors.Select(e => e.Message)));

        var first = await Jobs.DuplicateAsync(save.JobId);
        var second = await Jobs.DuplicateAsync(save.JobId);

        var repository = _services.GetRequiredService<IJobRepository>();
        var copy = await repository.GetAsync(first);
        Assert.Equal("Documentos (copia)", copy!.Name);
        Assert.Equal("Documentos (copia 2)", (await repository.GetAsync(second))!.Name);
        Assert.False(copy.Enabled);
        Assert.Equal(connectionId, copy.Source.ConnectionId);
        Assert.Equal("/datos", copy.Source.Settings["remotePath"]);
        Assert.Equal("clave-segura", copy.EncryptionPassphrase);
        Assert.Equal("0 3 * * 0", copy.Schedule);
    }

    [Fact]
    public async Task RestoreTarget_Secrets_AreStoredEncrypted_AndKeptWhenLeftEmpty()
    {
        var save = await Jobs.SaveAsync(new BackupJob
        {
            Name = "Ftp con restauración",
            Source = Binding("ftp", ("host", "nas.local"), ("user", "backup"), ("password", "origen"), ("remotePath", "/datos")),
            Destination = Binding("local", ("path", _root)),
            RestoreTarget = Binding("ftp", ("host", "copia.local"), ("user", "restaura"), ("password", "restaurar"), ("remotePath", "/copia")),
        });
        Assert.True(save.Success, string.Join("; ", save.Errors.Select(e => e.Message)));

        var draft = await Jobs.GetForEditAsync(save.JobId);
        Assert.True(draft!.HasStoredSecret(JobSecrets.RestoreKey("password")));
        Assert.False(draft.Job.RestoreTarget!.Settings.ContainsKey("password"));

        // Guardar de nuevo sin tocar la contraseña la conserva.
        draft.Job.RestoreTarget.Settings["remotePath"] = "/copia2";
        Assert.True((await Jobs.SaveAsync(draft.Job)).Success);
        var stored = await _services.GetRequiredService<IJobRepository>().GetAsync(save.JobId);
        Assert.Equal("restaurar", stored!.RestoreTarget!.Settings["password"]);
        Assert.Equal("/copia2", stored.RestoreTarget.Settings["remotePath"]);

        await using var db = await _services.GetRequiredService<IDbContextFactory<BackupDbContext>>().CreateDbContextAsync();
        Assert.DoesNotContain("restaurar", (await db.Jobs.SingleAsync()).RestoreSettingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionNames_AreUniquePerTenant()
    {
        await CreateFtpConnectionAsync("NAS", "x");
        var duplicate = new Connection { Name = "nas", ProviderKey = "ftp" };
        duplicate.Settings["host"] = "otro";
        duplicate.Settings["user"] = "u";

        var result = await Connections.SaveAsync(duplicate);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Field == "Name");
    }

    private async Task<Guid> CreateFtpConnectionAsync(string name, string password)
    {
        var connection = Connections.NewDraft("ftp").Connection;
        connection.Name = name;
        connection.Settings["host"] = "nas.local";
        connection.Settings["user"] = "backup";
        connection.Settings["password"] = password;
        var result = await Connections.SaveAsync(connection);
        Assert.True(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.ConnectionId;
    }

    private static ProviderBinding Binding(string key, params (string Key, string Value)[] settings)
    {
        var binding = new ProviderBinding { ProviderKey = key };
        foreach (var (k, v) in settings)
        {
            binding.Settings[k] = v;
        }

        return binding;
    }
}
