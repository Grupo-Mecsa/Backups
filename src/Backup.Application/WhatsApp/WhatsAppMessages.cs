using System.Globalization;
using Backup.Application.Runs;
using Backup.Domain.Runs;

namespace Backup.Application.WhatsApp;

/// <summary>Mensajes de alerta con el formato de WhatsApp (*negrita*, _cursiva_, ```bloque```).</summary>
public static class WhatsAppMessages
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es");

    public static string RunFinished(BackupRun run, string tenantName)
    {
        var (icon, label) = run.Status switch
        {
            RunStatus.Succeeded => ("✅", "completado"),
            RunStatus.Cancelled => ("⏹️", "cancelado"),
            RunStatus.Warning => ("⚠️", "completado con advertencias"),
            _ => ("❌", "falló"),
        };

        var lines = new List<string>
        {
            $"{icon} *Respaldo {label}*",
            $"*{Plain(run.JobName)}*{(tenantName.Length > 0 ? $" · {Plain(tenantName)}" : string.Empty)}",
            string.Empty,
            $"🕒 {run.StartedAt.UtcDateTime.ToString("dd MMM yyyy HH:mm", Culture)} UTC" +
                (run.Duration is { } d ? $" · {(int)d.TotalMinutes} min {d.Seconds} s" : string.Empty),
            $"▶️ {(run.Trigger == RunTrigger.Scheduled ? "Programado" : "Manual")}",
        };

        if (run.ArtifactName is not null)
        {
            lines.Add($"📦 {Plain(run.ArtifactName)} ({ByteSize.Format(run.SizeBytes)})");
        }

        if (run.Error is not null)
        {
            var insight = ErrorInsights.Explain(run.Error);
            lines.Add(string.Empty);
            lines.Add($"⚠️ *{Plain(insight.Title)}*");
            if (insight.Hint is not null)
            {
                lines.Add($"💡 {Plain(insight.Hint)}");
            }

            if (insight.Recognized)
            {
                lines.Add(string.Empty);
                lines.Add($"```{Truncate(run.Error.Replace("```", "'''", StringComparison.Ordinal), 900)}```");
            }
        }

        lines.Add(string.Empty);
        lines.Add("🛡️ _BackupHub_");
        return string.Join('\n', lines);
    }

    public static string Test(string userName, string tenantName) => string.Join('\n',
        "✅ *Prueba de alertas de BackupHub*",
        string.Empty,
        $"Este chat recibirá los avisos de respaldos de *{Plain(tenantName)}*.",
        $"Enviada por {Plain(userName)}.",
        string.Empty,
        "🛡️ _BackupHub_");

    /// <summary>Quita los caracteres de formato para que nombres como "*prod*" no rompan el mensaje.</summary>
    private static string Plain(string text) => text.Replace("*", "∗", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal).Replace("~", "-", StringComparison.Ordinal);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
