using Backup.Application.Security;
using Backup.Domain.Runs;
using Backup.Infrastructure.Notifications;
using Microsoft.AspNetCore.Components;

namespace Backup.Web.Components;

/// <summary>
/// Base para componentes que se refrescan en vivo cuando cambia una ejecución.
/// Centraliza la suscripción y liberación del evento (DRY) y el salto al hilo del renderizador.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    [Inject] private RunEvents Events { get; set; } = default!;
    [Inject] protected ICurrentUser CurrentUser { get; set; } = default!;

    protected override void OnInitialized() => Events.RunChanged += HandleRunChanged;

    protected abstract Task OnRunChangedAsync(BackupRun run);

    private void HandleRunChanged(BackupRun run)
    {
        // Solo eventos del tenant del usuario.
        if (!CurrentUser.Owns(run.TenantId))
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            await OnRunChangedAsync(run);
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        Events.RunChanged -= HandleRunChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
