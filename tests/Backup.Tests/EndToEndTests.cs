using System.IO.Compression;
using Backup.Application.Abstractions;
using Backup.Application.Providers;
using Backup.Application.Security;
using Backup.Application.Jobs;
using Backup.Application.Runs;
using Backup.Application.Telegram;
using Backup.Domain.Jobs;
using Backup.Domain.Runs;
using Backup.Infrastructure;
using Backup.Infrastructure.Transforms;
using Backup.Providers.Cloud;
using Backup.Providers.Databases;
using Backup.Providers.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backup.Tests;

/// <summary>
/// Ejecuta el pipeline completo con el cableado real (SQLite, Data Protection, proveedores).
/// Las pruebas contra S3/SFTP requieren <c>docker compose --profile demo up</c> y BACKUP_IT=1.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "backup-e2e-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = default!;
    private readonly TestUser _user = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeTelegramBot _telegram = new();

    private string SourceDir => Path.Combine(_root, "origen");
    private string DestinationDir => Path.Combine(_root, "destino");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(SourceDir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(SourceDir, "factura.txt"), "Factura 001");
        await File.WriteAllTextAsync(Path.Combine(SourceDir, "sub", "reporte.csv"), "a,b,c\n1,2,3");
        await File.WriteAllTextAsync(Path.Combine(SourceDir, "temporal.tmp"), "ignorar");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:WorkingDirectory"] = Path.Combine(_root, "work"),
                ["Smtp:Host"] = "smtp.plataforma.test",
                ["Smtp:FromAddress"] = "no-reply@plataforma.test",
            })
            .Build();

        _services = new ServiceCollection()
            .AddLogging()
            .AddBackupInfrastructure(configuration, Path.Combine(_root, "data"))
            .AddDatabaseProviders()
            .AddCloudProviders()
            .AddFileProviders()
            .AddScoped<ICurrentUser>(_ => _user)
            .AddSingleton<IEmailSender>(_email)
            .AddSingleton<ITelegramBot>(_telegram)
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
    public async Task LocalFolder_ToLocalFolder_CompressedEncrypted_WithRetention()
    {
        var jobs = _services.GetRequiredService<JobService>();
        var runner = _services.GetRequiredService<IBackupRunner>();

        var save = await jobs.SaveAsync(new BackupJob
        {
            Name = "Documentos",
            Source = Binding("local", ("path", SourceDir), ("exclude", "*.tmp")),
            Destination = Binding("local", ("path", DestinationDir)),
            Compression = CompressionKind.GZip,
            EncryptionPassphrase = "contraseña-segura",
            Retention = new RetentionPolicy { KeepLast = 2 },
        });
        Assert.True(save.Success, string.Join("; ", save.Errors.Select(e => e.Message)));

        BackupRun? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await runner.RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);
            Assert.True(last.Status == RunStatus.Succeeded, last.Error + "\n" + last.Log);
            await Task.Delay(1100); // timestamps distintos (resolución de 1 s)
        }

        var files = Directory.GetFiles(Path.Combine(DestinationDir, "documentos"));
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.EndsWith(".zip.gz.enc", f, StringComparison.Ordinal));
        Assert.Equal(1, last!.DeletedByRetention);

        // Restaurar: descifrar → descomprimir → abrir zip.
        await using var encrypted = File.OpenRead(Path.Combine(DestinationDir, last.ArtifactName!));
        using var decrypted = new MemoryStream();
        await BackupEncryption.DecryptAsync(encrypted, decrypted, "contraseña-segura");
        decrypted.Position = 0;
        using var unzipped = new MemoryStream();
        await using (var gzip = new GZipStream(decrypted, CompressionMode.Decompress))
        {
            await gzip.CopyToAsync(unzipped);
        }

        using var archive = new ZipArchive(unzipped);
        var entries = archive.Entries.Select(e => e.FullName).Order().ToList();
        Assert.Equal(new[] { "factura.txt", "sub/reporte.csv" }, entries);
    }

    [Fact]
    public async Task LocalFolder_WithExplorerSelection_BacksUpOnlySelectedItems()
    {
        Directory.CreateDirectory(Path.Combine(SourceDir, "sub", "secreto"));
        await File.WriteAllTextAsync(Path.Combine(SourceDir, "sub", "secreto", "clave.txt"), "no respaldar");

        var jobs = _services.GetRequiredService<JobService>();
        var selection = PathSelection.Empty;
        selection.Set("sub/secreto", included: false);
        selection.Set("temporal.tmp", included: false);

        var save = await jobs.SaveAsync(new BackupJob
        {
            Name = "Selectivo",
            Source = Binding("local", ("path", SourceDir), ("selection", selection.Serialize())),
            Destination = Binding("local", ("path", DestinationDir)),
            Compression = CompressionKind.None,
        });
        Assert.True(save.Success);

        var run = await _services.GetRequiredService<IBackupRunner>().RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);
        Assert.True(run.Status == RunStatus.Succeeded, run.Error);

        using var archive = ZipFile.OpenRead(Path.Combine(DestinationDir, run.ArtifactName!));
        Assert.Equal(new[] { "factura.txt", "sub/reporte.csv" }, archive.Entries.Select(e => e.FullName).Order().ToArray());

        // El explorador lista la carpeta con subcarpetas primero.
        var browse = await jobs.BrowseAsync(ProviderRole.Source, Binding("local", ("path", SourceDir)), null, SourceDir);
        Assert.NotNull(browse.Listing);
        Assert.Equal("sub", browse.Listing.Items[0].Name);
        Assert.True(browse.Listing.Items[0].IsDirectory);
    }

    [Fact]
    public async Task Secrets_AreEncryptedAtRest_AndStrippedForEditing()
    {
        var jobs = _services.GetRequiredService<JobService>();
        var save = await jobs.SaveAsync(new BackupJob
        {
            Name = "Con secreto",
            Source = Binding("local", ("path", SourceDir)),
            Destination = Binding("sftp", ("host", "ejemplo"), ("user", "u"), ("password", "p@ss-secreta")),
        });
        Assert.True(save.Success);

        var draft = await jobs.GetForEditAsync(save.JobId);
        Assert.NotNull(draft);
        Assert.False(draft.Job.Destination.Settings.ContainsKey("password"));
        Assert.True(draft.HasStoredSecret("destination:password"));

        // Guardar sin reingresar la contraseña la conserva.
        draft.Job.Description = "editado";
        Assert.True((await jobs.SaveAsync(draft.Job)).Success);
        var stored = await _services.GetRequiredService<IJobRepository>().GetAsync(save.JobId);
        Assert.Equal("p@ss-secreta", stored!.Destination.Settings["password"]);

        // En la base de datos no aparece en claro.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var db = new FileStream(Path.Combine(_root, "data", "backup.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(db);
        Assert.DoesNotContain("p@ss-secreta", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_ReportsMissingRequiredFieldsAndBadCron()
    {
        var result = await _services.GetRequiredService<JobService>().SaveAsync(new BackupJob
        {
            Name = "",
            Source = Binding("postgres"),
            Destination = new ProviderBinding(),
            Schedule = "esto no es cron",
        });

        Assert.False(result.Success);
        var fields = result.Errors.Select(e => e.Field).ToList();
        Assert.Contains("Name", fields);
        Assert.Contains("Source.host", fields);
        Assert.Contains("Destination", fields);
        Assert.Contains("Schedule", fields);
    }

    [Fact]
    public async Task Demo_S3AndSftpDestinations()
    {
        if (Environment.GetEnvironmentVariable("BACKUP_IT") != "1")
        {
            return; // requiere el perfil demo de docker compose
        }

        var jobs = _services.GetRequiredService<JobService>();
        var runner = _services.GetRequiredService<IBackupRunner>();
        var destinations = new[]
        {
            ("S3 demo", Binding("s3", ("bucket", "respaldos"), ("serviceUrl", "http://localhost:8333"), ("forcePathStyle", "true"),
                ("accessKey", "demo"), ("secretKey", "demo1234"), ("createBucket", "true"), ("prefix", "pruebas"))),
            ("SFTP demo", Binding("sftp", ("host", "localhost"), ("port", "2222"), ("user", "demo"), ("password", "demo1234"), ("remotePath", "/respaldos"))),
        };

        foreach (var (name, destination) in destinations)
        {
            var save = await jobs.SaveAsync(new BackupJob
            {
                Name = name,
                Source = Binding("local", ("path", SourceDir)),
                Destination = destination,
                Retention = new RetentionPolicy { KeepLast = 1 },
            });
            Assert.True(save.Success);

            var test = await jobs.TestConnectionAsync(ProviderRole.Destination, destination, save.JobId);
            Assert.True(test.Success, test.Message);

            // Explorar con la contraseña guardada (el formulario la envía vacía).
            var withoutSecrets = destination.Clone();
            withoutSecrets.Settings.Remove("password");
            withoutSecrets.Settings.Remove("secretKey");
            var browse = await jobs.BrowseAsync(ProviderRole.Destination, withoutSecrets, save.JobId, null);
            Assert.True(browse.Listing is not null, browse.Error);

            for (var i = 0; i < 2; i++)
            {
                var run = await runner.RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);
                Assert.True(run.Status == RunStatus.Succeeded, $"{name}: {run.Error}\n{run.Log}");
                await Task.Delay(1100);
            }
        }
    }

    [Fact]
    public async Task Tenants_AreIsolated_AndReadersCannotModify()
    {
        var jobs = _services.GetRequiredService<JobService>();
        var save = await jobs.SaveAsync(new BackupJob
        {
            Name = "Solo tenant A",
            Source = Binding("local", ("path", SourceDir)),
            Destination = Binding("local", ("path", DestinationDir)),
        });
        Assert.True(save.Success);
        var tenantA = _user.TenantId;

        // Otro tenant no ve ni puede tocar el trabajo.
        var tenants = _services.GetRequiredService<Application.Tenants.TenantService>();
        _user.Role = AppRoles.SuperAdmin;
        var (created, _, tenantB) = await tenants.SaveAsync(null, "Tenant B", enabled: true);
        Assert.True(created);

        _user.TenantId = tenantB;
        _user.Role = AppRoles.Admin;
        Assert.Empty(await jobs.ListAsync());
        Assert.Null(await jobs.GetForEditAsync(save.JobId));
        Assert.False(await jobs.RunNowAsync(save.JobId));
        await jobs.DeleteAsync(save.JobId);
        Assert.NotNull(await _services.GetRequiredService<IJobRepository>().GetAsync(save.JobId));

        // Un lector del tenant A ve el trabajo pero no puede modificarlo.
        _user.TenantId = tenantA;
        _user.Role = AppRoles.Reader;
        Assert.Single(await jobs.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => jobs.RunNowAsync(save.JobId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => jobs.SetEnabledAsync(save.JobId, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tenants.ListAsync());
        _user.Role = AppRoles.Admin;
    }

    [Fact]
    public async Task UserAdministration_EnforcesRolesAndLastAdmin()
    {
        var admin = _services.GetRequiredService<IUserAdministration>();
        var seeded = (await admin.ListAsync()).Single(); // SuperAdmin inicial
        Assert.Equal(AppRoles.SuperAdmin, seeded.Role);

        // Un Admin no puede crear SuperAdmins.
        var denied = await admin.SaveAsync(new UserEdit(null, "otro@empresa.com", "Otro", AppRoles.SuperAdmin, "Clave1234"));
        Assert.False(denied.Success);

        var reader = await admin.SaveAsync(new UserEdit(null, "lector@empresa.com", "Lectora", AppRoles.Reader, "Clave1234"));
        Assert.True(reader.Success, string.Join(" ", reader.Errors));
        var weak = await admin.SaveAsync(new UserEdit(null, "debil@empresa.com", "Débil", AppRoles.Reader, "123"));
        Assert.False(weak.Success);

        // Un Admin no puede eliminar al SuperAdmin.
        var deleteSuper = await admin.DeleteAsync(seeded.Id);
        Assert.False(deleteSuper.Success);

        var users = await admin.ListAsync();
        Assert.Contains(users, u => u.Email == "lector@empresa.com" && u.Role == AppRoles.Reader);
    }

    [Fact]
    public async Task Alerts_PasswordIsProtected_AndEmailTemplateDescribesFailure()
    {
        var notifications = _services.GetRequiredService<Application.Notifications.NotificationService>();
        var errors = await notifications.SaveAsync(new Domain.Notifications.NotificationSettings
        {
            Enabled = true,
            SmtpHost = "smtp.example.com",
            SmtpPort = 587,
            Username = "alertas@example.com",
            Password = "smtp-secreto",
            FromAddress = "alertas@example.com",
            Recipients = "ti@example.com; soporte@example.com",
        });
        Assert.Empty(errors);

        var (settings, hasPassword) = await notifications.GetAsync();
        Assert.True(hasPassword);
        Assert.Null(settings.Password);
        Assert.Equal(2, settings.RecipientList.Count);

        var invalid = await notifications.SaveAsync(new Domain.Notifications.NotificationSettings { Enabled = true, SmtpHost = "x", FromAddress = "no-es-correo", Recipients = "" });
        Assert.Contains(invalid, e => e.Field == "FromAddress");
        Assert.Contains(invalid, e => e.Field == "Recipients");

        var email = Application.Notifications.EmailTemplates.RunFinished(new BackupRun
        {
            JobName = "ERP <prod>",
            Status = RunStatus.Failed,
            Error = "Conexión rechazada",
            FinishedAt = DateTimeOffset.UtcNow,
        });
        Assert.Contains("falló", email.Subject, StringComparison.Ordinal);
        Assert.Contains("ERP &lt;prod&gt;", email.HtmlBody, StringComparison.Ordinal); // HTML escapado
        Assert.Contains("Conexión rechazada", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitation_LetsUserDefinePassword_AndLinkIsSingleUse()
    {
        var admin = _services.GetRequiredService<IUserAdministration>();
        Assert.True(await admin.CanSendEmailAsync()); // SMTP de plataforma configurado

        var created = await admin.SaveAsync(new UserEdit(null, "nuevo@empresa.com", "Nuevo", AppRoles.Reader, null, SendInvitation: true));
        Assert.True(created.Success, string.Join(" ", created.Errors));

        var (smtp, message) = Assert.Single(_email.Sent);
        Assert.Equal("smtp.plataforma.test", smtp.Host);
        Assert.Equal(["nuevo@empresa.com"], message.To);
        var link = new Uri(System.Text.RegularExpressions.Regex.Match(message.TextBody, @"https://\S+").Value);
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        Assert.Equal("1", query["invite"]);

        await using var scope = _services.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<Infrastructure.Identity.PasswordRecovery>();
        var weak = await recovery.ResetAsync(query["user"]!, query["code"]!, "corta");
        Assert.False(weak.Success);
        var ok = await recovery.ResetAsync(query["user"]!, query["code"]!, "NuevaClave123");
        Assert.True(ok.Success, string.Join(" ", ok.Errors));

        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByEmailAsync("nuevo@empresa.com"))!, "NuevaClave123"));

        var reused = await recovery.ResetAsync(query["user"]!, query["code"]!, "OtraClave123");
        Assert.False(reused.Success); // el token queda invalidado al cambiar la contraseña

        // "Olvidé mi contraseña" no revela si la cuenta existe.
        await recovery.RequestResetAsync("no-existe@empresa.com");
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task CreateInTenant_RequiresSuperAdmin()
    {
        var admin = _services.GetRequiredService<IUserAdministration>();
        var edit = new UserEdit(null, "jefe@cliente.com", "Jefe", AppRoles.Admin, "Clave1234");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => admin.CreateInTenantAsync(Guid.NewGuid(), edit));

        _user.Role = AppRoles.SuperAdmin;
        var (_, _, tenantB) = await _services.GetRequiredService<Application.Tenants.TenantService>().SaveAsync(null, "Cliente", enabled: true);
        var result = await admin.CreateInTenantAsync(tenantB, edit);
        Assert.True(result.Success, string.Join(" ", result.Errors));

        _user.TenantId = tenantB;
        _user.Role = AppRoles.Admin;
        Assert.Contains(await admin.ListAsync(), u => u.Email == "jefe@cliente.com" && u.Role == AppRoles.Admin);
    }

    [Fact]
    public async Task Alerts_CanUsePlatformSmtp()
    {
        var notifications = _services.GetRequiredService<Application.Notifications.NotificationService>();
        Assert.Equal("no-reply@plataforma.test", notifications.PlatformSender);
        var (defaults, _) = await notifications.GetAsync();
        Assert.True(defaults.UsePlatformSmtp);

        var errors = await notifications.SaveAsync(new Domain.Notifications.NotificationSettings
        {
            Enabled = true,
            UsePlatformSmtp = true,
            Recipients = "ti@example.com",
        });
        Assert.Empty(errors);

        var notifier = _services.GetServices<IRunNotifier>().OfType<Application.Notifications.EmailRunNotifier>().Single();
        await notifier.NotifyAsync(new BackupRun { TenantId = _user.TenantId, JobName = "ERP", Status = RunStatus.Failed, Error = "x" }, CancellationToken.None);
        await Eventually.TrueAsync(() => !_email.Sent.IsEmpty);
        var (smtp, message) = Assert.Single(_email.Sent);
        Assert.Equal("smtp.plataforma.test", smtp.Host);
        Assert.Equal(["ti@example.com"], message.To);
    }

    [Fact]
    public async Task Telegram_LinkNotifyAndStop()
    {
        var telegram = _services.GetRequiredService<TelegramService>();
        var commands = _services.GetRequiredService<TelegramBotCommands>();
        var link = telegram.CreateLink();
        Assert.Equal($"https://t.me/backuphub_test_bot?start={link.Code}", link.PrivateUrl);

        // Código inválido y luego el válido (en un grupo llega como /start@bot CODIGO).
        Assert.Equal(TelegramMessages.InvalidCode, await commands.HandleAsync(new TelegramIncoming(100, "Soporte TI", true, "/start@backuphub_test_bot nope")));
        var reply = await commands.HandleAsync(new TelegramIncoming(100, "Soporte TI", true, $"/start@backuphub_test_bot {link.Code}"));
        Assert.Contains("vinculado", reply, StringComparison.Ordinal);
        Assert.Equal(TelegramMessages.InvalidCode, await commands.HandleAsync(new TelegramIncoming(100, "Soporte TI", true, $"/start {link.Code}"))); // un solo uso

        var chat = Assert.Single(await telegram.ListMineAsync());
        Assert.True(chat is { IsGroup: true, NotifyOnFailure: true, NotifyOnSuccess: false });

        // Un éxito no se notifica (preferencia por defecto); un fallo sí.
        var notifier = _services.GetServices<IRunNotifier>().OfType<TelegramRunNotifier>().Single();
        await notifier.NotifyAsync(new BackupRun { TenantId = _user.TenantId, JobName = "ERP", Status = RunStatus.Succeeded }, CancellationToken.None);
        await notifier.NotifyAsync(new BackupRun { TenantId = _user.TenantId, JobName = "ERP <prod>", Status = RunStatus.Failed, Error = "Disco lleno" }, CancellationToken.None);
        await Eventually.TrueAsync(() => !_telegram.Sent.IsEmpty);
        var (chatId, html) = Assert.Single(_telegram.Sent);
        Assert.Equal(100, chatId);
        Assert.Contains("ERP &lt;prod&gt;", html, StringComparison.Ordinal);

        // Otro tenant no ve el chat; un lector no puede tocar chats ajenos.
        _user.Role = AppRoles.Reader;
        _user.UserId = "otro-usuario";
        Assert.Empty(await telegram.ListMineAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => telegram.DeleteAsync(chat.Id));
        _user.Role = AppRoles.Admin;
        _user.UserId = "test-user";

        Assert.Contains("Principal", await commands.HandleAsync(new TelegramIncoming(100, "Soporte TI", true, "/estado")), StringComparison.Ordinal); // tenant por defecto
        await commands.HandleAsync(new TelegramIncoming(100, "Soporte TI", true, "/stop"));
        Assert.Empty(await telegram.ListMineAsync());
    }

    [Fact]
    public async Task Telegram_BlockedChatIsForgotten()
    {
        var telegram = _services.GetRequiredService<TelegramService>();
        var commands = _services.GetRequiredService<TelegramBotCommands>();
        await commands.HandleAsync(new TelegramIncoming(200, "@ana", false, $"/start {telegram.CreateLink().Code}"));
        _telegram.BlockedChats.Add(200);

        var notifier = _services.GetServices<IRunNotifier>().OfType<TelegramRunNotifier>().Single();
        await notifier.NotifyAsync(new BackupRun { TenantId = _user.TenantId, JobName = "ERP", Status = RunStatus.Failed }, CancellationToken.None);
        await Eventually.TrueAsync(() => telegram.ListMineAsync().GetAwaiter().GetResult().Count == 0);
    }

    [Fact]
    public async Task Registration_FirstUserOfNewOrganizationIsApproved_JoiningExistingNeedsApproval()
    {
        await using var scope = _services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<Infrastructure.Identity.SelfRegistration>();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        Assert.Equal(RegistrationMode.Enabled, registration.Mode);

        var (weak, _, _) = await registration.RegisterAsync("Acme", "Ana", "ana@acme.com", "123");
        Assert.False(weak.Success);
        Assert.Equal(1, (await tenants.ListAsync()).Count); // no deja tenants huérfanos

        // Organización nueva: su primer usuario entra de inmediato como administrador.
        var (ok, ana, canSignIn) = await registration.RegisterAsync("Acme", "Ana", "ana@acme.com", "Clave1234");
        Assert.True(ok.Success, string.Join(" ", ok.Errors));
        Assert.True(canSignIn);
        Assert.False(ana!.PendingApproval);
        Assert.True(await tenants.GetAsync(ana.TenantId) is { Enabled: true, PendingApproval: false });
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
        Assert.True(await users.IsInRoleAsync(ana, AppRoles.Admin));

        // Organización existente (sin importar mayúsculas): lector pendiente y aviso a sus administradores.
        var (joined, beto, betoCanSignIn) = await registration.RegisterAsync("acme", "Beto", "beto@acme.com", "Clave1234");
        Assert.True(joined.Success, string.Join(" ", joined.Errors));
        Assert.False(betoCanSignIn);
        Assert.True(beto!.PendingApproval);
        Assert.Equal(ana.TenantId, beto.TenantId);
        Assert.True(await users.IsInRoleAsync(beto, AppRoles.Reader));
        var (_, notice) = Assert.Single(_email.Sent);
        Assert.Equal(["ana@acme.com"], notice.To);

        // Un administrador de Acme aprueba; un pendiente no cuenta como administrador activo.
        _user.TenantId = ana.TenantId;
        var admin = _services.GetRequiredService<IUserAdministration>();
        Assert.Contains(await admin.ListAsync(), u => u.Email == "beto@acme.com" && u.IsPending);
        Assert.True((await admin.ApproveAsync(beto.Id)).Success);
        Assert.Contains(await admin.ListAsync(), u => u.Email == "beto@acme.com" && !u.IsPending);
    }

    [Fact]
    public async Task Artifact_CanBeReviewed_DownloadedAndVerified()
    {
        var jobs = _services.GetRequiredService<JobService>();
        var save = await jobs.SaveAsync(new BackupJob
        {
            Name = "Revisable",
            Source = Binding("local", ("path", SourceDir), ("exclude", "*.tmp")),
            Destination = Binding("local", ("path", DestinationDir)),
            Compression = CompressionKind.GZip,
            EncryptionPassphrase = "contraseña-segura",
        });
        Assert.True(save.Success);
        var run = await _services.GetRequiredService<IBackupRunner>().RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);
        Assert.True(run.Status == RunStatus.Succeeded, run.Error);

        var artifacts = _services.GetRequiredService<ArtifactService>();
        Assert.True(artifacts.IsTransformed(run));
        Assert.EndsWith(".zip", artifacts.DecodedName(run), StringComparison.Ordinal);

        // Contenido del .zip, descifrado y descomprimido.
        var contents = await artifacts.GetContentsAsync(run.Id);
        Assert.Equal(ArtifactContentKind.Archive, contents.Kind);
        Assert.Equal(new[] { "factura.txt", "sub/reporte.csv" }, contents.Entries.Select(e => e.Path).ToArray());

        // Un archivo suelto.
        var entry = await artifacts.ExtractEntryAsync(run.Id, _user.TenantId, "factura.txt");
        Assert.Equal("Factura 001", await File.ReadAllTextAsync(entry.FilePath));

        // Tal cual (cifrado) y listo para usar (.zip).
        var raw = await artifacts.PrepareAsync(run.Id, _user.TenantId, decoded: false);
        Assert.EndsWith(".zip.gz.enc", raw.DownloadName, StringComparison.Ordinal);
        var decoded = await artifacts.PrepareAsync(run.Id, _user.TenantId, decoded: true);
        using (var zip = ZipFile.OpenRead(decoded.FilePath))
        {
            Assert.Equal(2, zip.Entries.Count(e => !e.FullName.EndsWith('/')));
        }

        // Otro tenant no la ve.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => artifacts.PrepareAsync(run.Id, Guid.NewGuid(), decoded: false));
    }

    [Fact]
    public async Task Artifact_AlteredInDestination_IsRejectedBySha256()
    {
        var jobs = _services.GetRequiredService<JobService>();
        var save = await jobs.SaveAsync(new BackupJob
        {
            Name = "Alterable",
            Source = Binding("local", ("path", SourceDir)),
            Destination = Binding("local", ("path", DestinationDir)),
            Compression = CompressionKind.None,
        });
        Assert.True(save.Success);
        var run = await _services.GetRequiredService<IBackupRunner>().RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);
        Assert.True(run.Status == RunStatus.Succeeded, run.Error);

        await File.AppendAllTextAsync(Path.Combine(DestinationDir, run.ArtifactName!), "alterado");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _services.GetRequiredService<ArtifactService>().PrepareAsync(run.Id, _user.TenantId, decoded: false));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_ZipBackup_ToLocalFolder_KeepsExistingFilesUnlessOverwrite()
    {
        var jobs = _services.GetRequiredService<JobService>();
        var save = await jobs.SaveAsync(new BackupJob
        {
            Name = "Restaurable",
            Source = Binding("local", ("path", SourceDir), ("exclude", "*.tmp")),
            Destination = Binding("local", ("path", DestinationDir)),
            Compression = CompressionKind.GZip,
            EncryptionPassphrase = "contraseña-segura",
        });
        Assert.True(save.Success);
        var run = await _services.GetRequiredService<IBackupRunner>().RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);
        Assert.True(run.Status == RunStatus.Succeeded, run.Error);

        var restores = _services.GetRequiredService<RestoreService>();
        Assert.Contains(restores.TargetsFor(run), t => t.Descriptor.Key == "local");

        // Un archivo ya existente en el destino de la restauración: sin "sobrescribir" se conserva.
        var target = Path.Combine(_root, "restaurado");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "factura.txt"), "versión local");

        var first = await StartAndWaitAsync(restores, run.Id, Binding("local", ("path", target)));
        Assert.True(first.Status == RunStatus.Succeeded, first.Error + "\n" + first.Log);
        Assert.Equal("versión local", await File.ReadAllTextAsync(Path.Combine(target, "factura.txt")));
        Assert.Equal("a,b,c\n1,2,3", await File.ReadAllTextAsync(Path.Combine(target, "sub", "reporte.csv")));
        Assert.Contains("ya existían", first.Log, StringComparison.Ordinal);

        var second = await StartAndWaitAsync(restores, run.Id, Binding("local", ("path", target), ("overwrite", "true")));
        Assert.Equal(RunStatus.Succeeded, second.Status);
        Assert.Equal("Factura 001", await File.ReadAllTextAsync(Path.Combine(target, "factura.txt")));

        // Queda en el historial, con quién la pidió y sin secretos.
        var history = await restores.ListAsync();
        Assert.Equal(2, history.Count);
        Assert.All(history, r => Assert.Equal("Restaurable", r.JobName));
    }

    [Fact]
    public async Task Restore_RequiresTargetFields()
    {
        var save = await _services.GetRequiredService<JobService>().SaveAsync(new BackupJob
        {
            Name = "SinDestino",
            Source = Binding("local", ("path", SourceDir)),
            Destination = Binding("local", ("path", DestinationDir)),
        });
        var run = await _services.GetRequiredService<IBackupRunner>().RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);

        var result = await _services.GetRequiredService<RestoreService>().StartAsync(run.Id, Binding("local"), RestoreSecrets.None);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Field == "Target.path");
    }

    [Fact]
    public async Task Restore_Presets_JobTargetAndSource()
    {
        var target = Path.Combine(_root, "copia-fija");
        var save = await _services.GetRequiredService<JobService>().SaveAsync(new BackupJob
        {
            Name = "ConDestinoFijo",
            Source = Binding("local", ("path", SourceDir), ("exclude", "*.tmp")),
            Destination = Binding("local", ("path", DestinationDir)),
            RestoreTarget = Binding("local", ("path", target)),
        });
        Assert.True(save.Success, string.Join("; ", save.Errors.Select(e => e.Message)));
        var run = await _services.GetRequiredService<IBackupRunner>().RunAsync(save.JobId, RunTrigger.Manual, CancellationToken.None);

        var restores = _services.GetRequiredService<RestoreService>();
        var presets = await restores.PresetsAsync(run);
        var jobTarget = presets.Single(p => p.Preset == RestorePreset.JobTarget);
        var source = presets.Single(p => p.Preset == RestorePreset.Source);
        Assert.Equal(target, jobTarget.Binding.Settings["path"]);
        Assert.Equal(SourceDir, source.Binding.Settings["path"]);

        // El destino del trabajo, tal cual viene: se levanta ahí.
        var restore = await StartAndWaitAsync(restores, run.Id, jobTarget.Binding, new RestoreSecrets(save.JobId, RestorePreset.JobTarget));
        Assert.True(restore.Status == RunStatus.Succeeded, restore.Error + "\n" + restore.Log);
        Assert.Equal("Factura 001", await File.ReadAllTextAsync(Path.Combine(target, "factura.txt")));
    }

    [Fact]
    public async Task RestoreTarget_ThatCannotRestoreTheSourceBackup_IsRejected()
    {
        var save = await _services.GetRequiredService<JobService>().SaveAsync(new BackupJob
        {
            Name = "DestinoIncompatible",
            Source = Binding("local", ("path", SourceDir)),
            Destination = Binding("local", ("path", DestinationDir)),
            RestoreTarget = Binding("postgres", ("host", "db"), ("user", "postgres"), ("database", "x")),
        });

        Assert.False(save.Success);
        Assert.Contains(save.Errors, e => e.Field == "Restore");
    }

    private Task<Domain.Runs.RestoreOperation> StartAndWaitAsync(RestoreService restores, Guid runId, ProviderBinding target) =>
        StartAndWaitAsync(restores, runId, target, RestoreSecrets.None);

    private async Task<Domain.Runs.RestoreOperation> StartAndWaitAsync(RestoreService restores, Guid runId, ProviderBinding target, RestoreSecrets secrets)
    {
        var start = await restores.StartAsync(runId, target, secrets);
        Assert.True(start.Success, string.Join("; ", start.Errors.Select(e => e.Message)));
        for (var i = 0; i < 100; i++)
        {
            var restore = await restores.GetAsync(start.RestoreId);
            if (restore is { Status: not RunStatus.Running })
            {
                return restore;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("La restauración no terminó a tiempo.");
    }

    private static ProviderBinding Binding(string key, params (string Key, string? Value)[] settings)
    {
        var binding = new ProviderBinding { ProviderKey = key };
        foreach (var (k, v) in settings)
        {
            binding.Settings[k] = v;
        }

        return binding;
    }
}
