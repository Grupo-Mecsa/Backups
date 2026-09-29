using Backup.Domain.Runs;

namespace Backup.Application.Abstractions;

/// <summary>Cifra valores sensibles antes de persistirlos.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>Ejecuta herramientas externas (pg_dump, mysqldump, mongodump...).</summary>
public interface IProcessRunner
{
    /// <param name="stdoutFile">Si se indica, la salida estándar se escribe en ese archivo.</param>
    /// <param name="environment">Variables de entorno adicionales (p. ej. PGPASSWORD).</param>
    /// <exception cref="ProcessFailedException">Si el proceso termina con código distinto de 0.</exception>
    Task RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdoutFile = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessFailedException(string message) : Exception(message);

/// <summary>Interpreta expresiones cron.</summary>
public interface IScheduleCalculator
{
    bool TryValidate(string? expression, string timeZone, out string? error);
    DateTimeOffset? GetNextOccurrence(string expression, string timeZone, DateTimeOffset from);
}

/// <summary>Observador de cambios en las ejecuciones (UI en vivo, webhooks, correo...).</summary>
public interface IRunNotifier
{
    Task NotifyAsync(BackupRun run, CancellationToken cancellationToken);
}

/// <summary>Cola de trabajos por ejecutar, compartida entre la UI y el planificador.</summary>
public interface IBackupQueue
{
    /// <returns>False si el trabajo ya está en cola o en ejecución.</returns>
    bool TryEnqueue(Guid jobId, RunTrigger trigger);

    bool IsBusy(Guid jobId);

    /// <summary>Solicita cancelar la ejecución en curso del trabajo.</summary>
    bool Cancel(Guid jobId);
}

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string TextBody);

/// <summary>Envía un correo a los destinatarios de <see cref="EmailMessage.To"/>.</summary>
public interface IEmailSender
{
    Task SendAsync(Domain.Notifications.SmtpServer smtp, EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Señal para que el planificador recalcule horarios cuando cambian los trabajos.</summary>
public interface IScheduleSignal
{
    void Changed();
}
