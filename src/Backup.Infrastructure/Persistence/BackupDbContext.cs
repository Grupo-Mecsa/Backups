using Backup.Domain.Notifications;
using Backup.Domain.Runs;
using Backup.Domain.Tenants;
using Backup.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Backup.Infrastructure.Persistence;

public sealed class BackupDbContext(DbContextOptions<BackupDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<BackupRun> Runs => Set<BackupRun>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<TelegramSubscription> TelegramSubscriptions => Set<TelegramSubscription>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite no ordena ni compara DateTimeOffset de forma nativa: se guarda como entero.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // tablas de ASP.NET Core Identity

        modelBuilder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(100);
            user.HasIndex(u => u.TenantId);
        });

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("Tenants");
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Name).HasMaxLength(100).IsRequired();
            tenant.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<NotificationSettings>(settings =>
        {
            settings.ToTable("NotificationSettings");
            settings.HasKey(s => s.TenantId);
            settings.Ignore(s => s.RecipientList);
            settings.Ignore(s => s.HasOwnSmtp);
            settings.Ignore(s => s.OwnSmtp);
            settings.Property(s => s.Security).HasConversion<string>().HasMaxLength(20);
            settings.Property(s => s.SmtpHost).HasMaxLength(255);
            settings.Property(s => s.Username).HasMaxLength(255);
            settings.Property(s => s.FromAddress).HasMaxLength(255);
            settings.Property(s => s.FromName).HasMaxLength(100);
            settings.Property(s => s.Recipients).HasMaxLength(2000);
        });

        modelBuilder.Entity<TelegramSubscription>(subscription =>
        {
            subscription.ToTable("TelegramSubscriptions");
            subscription.HasKey(s => s.Id);
            subscription.Property(s => s.UserId).HasMaxLength(450).IsRequired();
            subscription.Property(s => s.ChatTitle).HasMaxLength(200);
            subscription.HasIndex(s => new { s.TenantId, s.ChatId }).IsUnique();
            subscription.HasIndex(s => s.ChatId);
        });

        modelBuilder.Entity<JobRecord>(job =>
        {
            job.ToTable("Jobs");
            job.HasKey(j => j.Id);
            job.Property(j => j.Name).HasMaxLength(100).IsRequired();
            job.Property(j => j.Description).HasMaxLength(500);
            job.Property(j => j.SourceProvider).HasMaxLength(50).IsRequired();
            job.Property(j => j.DestinationProvider).HasMaxLength(50).IsRequired();
            job.Property(j => j.Schedule).HasMaxLength(100);
            job.Property(j => j.TimeZone).HasMaxLength(100);
            job.Property(j => j.Compression).HasConversion<string>().HasMaxLength(20);
            job.HasIndex(j => new { j.TenantId, j.Name });
        });

        modelBuilder.Entity<BackupRun>(run =>
        {
            run.ToTable("Runs");
            run.HasKey(r => r.Id);
            run.Ignore(r => r.Duration);
            run.Property(r => r.JobName).HasMaxLength(100);
            run.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            run.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(20);
            run.Property(r => r.ArtifactName).HasMaxLength(500);
            run.Property(r => r.Sha256).HasMaxLength(64);
            run.HasIndex(r => new { r.JobId, r.StartedAt });
            run.HasIndex(r => new { r.TenantId, r.StartedAt });
            run.HasIndex(r => r.StartedAt);
        });
    }
}

/// <summary>Fila de persistencia de un trabajo. La configuración de proveedores se guarda como JSON.</summary>
public sealed class JobRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public string SourceProvider { get; set; } = string.Empty;
    public string SourceSettingsJson { get; set; } = "{}";
    public string DestinationProvider { get; set; } = string.Empty;
    public string DestinationSettingsJson { get; set; } = "{}";
    public string? Schedule { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public Domain.Jobs.CompressionKind Compression { get; set; }
    public string? EncryptionPassphrase { get; set; }
    public int KeepLast { get; set; }
    public int KeepDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
