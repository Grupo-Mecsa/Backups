using System.Globalization;
using Backup.Domain.Runs;

namespace Backup.Application.Telegram;

/// <summary>Mensajes del bot en el HTML limitado de Telegram (b, i, code, pre, a).</summary>
public static class TelegramMessages
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es");

    public static string RunFinished(BackupRun run, string tenantName)
    {
        var (icon, label) = run.Status switch
        {
            RunStatus.Succeeded => ("✅", "completado"),
            RunStatus.Cancelled => ("⚠️", "cancelado"),
            RunStatus.Warning => ("⚠️", "completado con advertencias"),
            _ => ("❌", "falló"),
        };

        var lines = new List<string>
        {
            $"{icon} <b>Respaldo {label}</b>",
            $"<b>{E(run.JobName)}</b> · {E(tenantName)}",
            string.Empty,
            $"🕒 {run.StartedAt.UtcDateTime.ToString("dd MMM yyyy HH:mm", Culture)} UTC" +
                (run.Duration is { } d ? $" · {(int)d.TotalMinutes} min {d.Seconds} s" : string.Empty),
            $"▶️ {(run.Trigger == RunTrigger.Scheduled ? "Programado" : "Manual")}",
        };

        if (run.ArtifactName is not null)
        {
            lines.Add($"📦 <code>{E(run.ArtifactName)}</code> ({ByteSize.Format(run.SizeBytes)})");
        }

        if (run.Error is not null)
        {
            lines.Add(string.Empty);
            lines.Add($"<pre>{E(Truncate(run.Error, 1500))}</pre>");
        }

        return string.Join('\n', lines);
    }

    public static string Linked(string tenantName, bool onFailure, bool onSuccess) =>
        $"""
        ✅ <b>Chat vinculado a BackupHub</b>
        Recibirás las alertas de <b>{E(tenantName)}</b>: {Describe(onFailure, onSuccess)}.

        Ajusta las preferencias en <i>Mi cuenta → Telegram</i>. Envía /stop para dejar de recibirlas.
        """;

    public static string Test(string tenantName) => $"🔔 <b>Prueba de BackupHub</b>\nEste chat recibe las alertas de <b>{E(tenantName)}</b>.";

    public const string InvalidCode = "⛔ El código no es válido o caducó. Genera uno nuevo en <i>Mi cuenta → Telegram</i>.";

    public const string Help = """
        🤖 <b>Bot de alertas de BackupHub</b>

        Para suscribirte abre <i>Mi cuenta → Telegram</i> en BackupHub y pulsa <b>Conectar</b>.
        /estado — suscripciones de este chat
        /stop — dejar de recibir alertas
        """;

    public static string Status(IReadOnlyList<(string Tenant, bool OnFailure, bool OnSuccess)> subscriptions) =>
        subscriptions.Count == 0
            ? "Este chat no recibe alertas. Vincúlalo desde <i>Mi cuenta → Telegram</i>."
            : "📋 <b>Este chat recibe alertas de:</b>\n" + string.Join('\n', subscriptions.Select(s => $"• <b>{E(s.Tenant)}</b>: {Describe(s.OnFailure, s.OnSuccess)}"));

    public static string Stopped(int count) => count == 0
        ? "Este chat no tenía alertas activas."
        : "🔕 Listo, este chat ya no recibirá alertas de BackupHub.";

    private static string Describe(bool onFailure, bool onSuccess) => (onFailure, onSuccess) switch
    {
        (true, true) => "fallos y respaldos exitosos",
        (true, false) => "solo fallos",
        (false, true) => "solo respaldos exitosos",
        _ => "ninguna (pausado)",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    /// <summary>Telegram solo requiere escapar &amp;, &lt; y &gt; (WebUtility también codificaría los acentos).</summary>
    private static string E(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
