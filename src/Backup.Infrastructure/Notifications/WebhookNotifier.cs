using System.Net.Http.Json;
using Backup.Application;
using Backup.Application.Abstractions;
using Backup.Domain.Runs;
using Microsoft.Extensions.Options;

namespace Backup.Infrastructure.Notifications;

public sealed class WebhookOptions
{
    public const string SectionName = "Notifications:Webhook";

    /// <summary>URL que recibe un POST JSON al terminar cada ejecución. Vacío = deshabilitado.</summary>
    public string? Url { get; set; }

    public bool OnlyOnFailure { get; set; }
}

/// <summary>Envía el resultado de cada ejecución a un webhook (Teams, Slack, n8n, etc.).</summary>
public sealed class WebhookNotifier(IHttpClientFactory httpClientFactory, IOptionsMonitor<WebhookOptions> options) : IRunNotifier
{
    public async Task NotifyAsync(BackupRun run, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.Url) || run.Status == RunStatus.Running)
        {
            return;
        }

        if (settings.OnlyOnFailure && run.Status == RunStatus.Succeeded)
        {
            return;
        }

        var client = httpClientFactory.CreateClient(nameof(WebhookNotifier));
        var payload = new
        {
            text = run.Status == RunStatus.Succeeded
                ? $"✅ Respaldo '{run.JobName}' completado ({ByteSize.Format(run.SizeBytes)})."
                : $"❌ Respaldo '{run.JobName}' {(run.Status == RunStatus.Cancelled ? "cancelado" : "falló")}: {run.Error}",
            job = run.JobName,
            jobId = run.JobId,
            runId = run.Id,
            status = run.Status.ToString(),
            trigger = run.Trigger.ToString(),
            startedAt = run.StartedAt,
            finishedAt = run.FinishedAt,
            artifact = run.ArtifactName,
            sizeBytes = run.SizeBytes,
            sha256 = run.Sha256,
            error = run.Error,
        };

        using var response = await client.PostAsJsonAsync(new Uri(settings.Url), payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
