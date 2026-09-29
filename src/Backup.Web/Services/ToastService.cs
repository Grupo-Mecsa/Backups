namespace Backup.Web.Services;

public enum ToastTone
{
    Success,
    Error,
    Info,
}

public sealed record Toast(Guid Id, ToastTone Tone, string Title, string? Message);

/// <summary>Notificaciones efímeras por circuito (usuario conectado).</summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = [];

    public event Action? Changed;

    public IReadOnlyList<Toast> Toasts => _toasts;

    public void Success(string title, string? message = null) => Show(ToastTone.Success, title, message);
    public void Error(string title, string? message = null) => Show(ToastTone.Error, title, message, TimeSpan.FromSeconds(8));
    public void Info(string title, string? message = null) => Show(ToastTone.Info, title, message);

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    private void Show(ToastTone tone, string title, string? message, TimeSpan? duration = null)
    {
        var toast = new Toast(Guid.NewGuid(), tone, title, message);
        _toasts.Add(toast);
        Changed?.Invoke();
        _ = DismissLaterAsync(toast.Id, duration ?? TimeSpan.FromSeconds(4));
    }

    private async Task DismissLaterAsync(Guid id, TimeSpan delay)
    {
        await Task.Delay(delay);
        Dismiss(id);
    }
}
