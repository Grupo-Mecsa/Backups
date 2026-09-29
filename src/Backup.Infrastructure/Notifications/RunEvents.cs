using Backup.Application.Abstractions;
using Backup.Domain.Runs;

namespace Backup.Infrastructure.Notifications;

/// <summary>Bus de eventos en proceso: la UI se suscribe para refrescarse en vivo.</summary>
public sealed class RunEvents : IRunNotifier
{
    public event Action<BackupRun>? RunChanged;

    public Task NotifyAsync(BackupRun run, CancellationToken cancellationToken)
    {
        RunChanged?.Invoke(run);
        return Task.CompletedTask;
    }
}
