using System.Globalization;
using System.Net;
using Backup.Application.Abstractions;
using Backup.Domain.Runs;

namespace Backup.Application.Notifications;

/// <summary>Plantillas HTML (con estilos en línea, compatibles con clientes de correo) y su versión en texto.</summary>
public static class EmailTemplates
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es");

    public static EmailMessage RunFinished(BackupRun run)
    {
        var (color, label, icon) = run.Status switch
        {
            RunStatus.Succeeded => ("#12a150", "completado", "✅"),
            RunStatus.Cancelled => ("#d98a04", "cancelado", "⚠️"),
            RunStatus.Warning => ("#d98a04", "completado con advertencias", "⚠️"),
            _ => ("#e5484d", "falló", "❌"),
        };

        var subject = $"{icon} Respaldo \"{run.JobName}\" {label}";
        var rows = new List<(string, string)>
        {
            ("Trabajo", run.JobName),
            ("Estado", label),
            ("Inicio (UTC)", run.StartedAt.UtcDateTime.ToString("dd MMM yyyy HH:mm:ss", Culture)),
            ("Duración", run.Duration is { } d ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : "—"),
            ("Origen", run.Trigger == RunTrigger.Scheduled ? "Programado" : "Manual"),
        };

        if (run.ArtifactName is not null)
        {
            rows.Add(("Archivo", run.ArtifactName));
            rows.Add(("Tamaño", ByteSize.Format(run.SizeBytes)));
        }

        if (run.Error is not null)
        {
            rows.Add(("Error", run.Error));
        }

        var logTail = string.Join('\n', run.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(15));
        var html = Layout(color, subject, $"""
            <table role="presentation" cellpadding="0" cellspacing="0" style="width:100%;border-collapse:collapse;font-size:14px">
              {string.Concat(rows.Select(r => $"""
                <tr>
                  <td style="padding:8px 0;color:#6b7289;width:130px;vertical-align:top">{Encode(r.Item1)}</td>
                  <td style="padding:8px 0;color:#151827;font-weight:600;word-break:break-all">{Encode(r.Item2)}</td>
                </tr>
                """))}
            </table>
            {(logTail.Length == 0 ? string.Empty : $"""
              <p style="margin:20px 0 8px;color:#6b7289;font-size:12px;text-transform:uppercase;letter-spacing:.05em">Bitácora</p>
              <pre style="margin:0;padding:14px;background:#0b0d15;color:#c9cee0;border-radius:10px;font-size:12px;line-height:1.6;white-space:pre-wrap;word-break:break-word">{Encode(logTail)}</pre>
              """)}
            """);

        var text = string.Join('\n', rows.Select(r => $"{r.Item1}: {r.Item2}")) + (logTail.Length > 0 ? $"\n\nBitácora:\n{logTail}" : string.Empty);
        return new EmailMessage([], subject, html, text);
    }

    public static EmailMessage Test(string? requestedBy)
    {
        const string subject = "✅ Prueba de alertas de BackupHub";
        var body = $"La configuración SMTP funciona correctamente. Solicitado por {requestedBy ?? "un administrador"}.";
        return new EmailMessage([], subject, Layout("#5b5bd6", subject, $"""<p style="margin:0;font-size:14px;color:#151827">{Encode(body)}</p>"""), body);
    }

    /// <param name="link">Enlace para definir la contraseña (caduca en <paramref name="validFor"/>).</param>
    public static EmailMessage Invitation(string to, string tenantName, string? invitedBy, string link, TimeSpan validFor)
    {
        var subject = $"Te invitaron a BackupHub · {tenantName}";
        var intro = $"{invitedBy ?? "Un administrador"} te dio acceso a los respaldos de {tenantName}. Define tu contraseña para entrar.";
        return AccountEmail(to, subject, intro, "Definir contraseña", link, validFor, "Tu usuario es este correo.");
    }

    public static EmailMessage PasswordReset(string to, string link, TimeSpan validFor) => AccountEmail(
        to,
        "Restablecer tu contraseña de BackupHub",
        "Recibimos una solicitud para restablecer tu contraseña.",
        "Elegir nueva contraseña",
        link,
        validFor,
        "Si no la pediste, ignora este correo: tu contraseña actual sigue funcionando.");

    public static EmailMessage RegistrationPending(IReadOnlyList<string> to, string organization, string name, string email, string link)
    {
        var subject = $"Solicitud para unirse a {organization}";
        var body = $"{name} ({email}) se registró y pidió unirse a {organization}. Apruébala o recházala en Usuarios.";
        var html = Layout("#d98a04", subject, $"""
            <p style="margin:0 0 20px;font-size:14px;line-height:1.6;color:#151827">{Encode(body)}</p>
            <p style="margin:0"><a href="{Encode(link)}" style="display:inline-block;padding:12px 22px;background:#5b5bd6;color:#ffffff;border-radius:10px;font-size:14px;font-weight:600;text-decoration:none">Revisar solicitud</a></p>
            """);
        return new EmailMessage(to, subject, html, $"{body}\n\nRevísala en: {link}");
    }

    private static EmailMessage AccountEmail(string to, string subject, string intro, string action, string link, TimeSpan validFor, string footnote)
    {
        var expiry = $"El enlace caduca en {(validFor.TotalHours >= 48 ? $"{(int)validFor.TotalDays} días" : $"{(int)validFor.TotalHours} horas")}.";
        var html = Layout("#5b5bd6", subject, $"""
            <p style="margin:0 0 20px;font-size:14px;line-height:1.6;color:#151827">{Encode(intro)}</p>
            <p style="margin:0 0 20px"><a href="{Encode(link)}" style="display:inline-block;padding:12px 22px;background:#5b5bd6;color:#ffffff;border-radius:10px;font-size:14px;font-weight:600;text-decoration:none">{Encode(action)}</a></p>
            <p style="margin:0 0 6px;font-size:12px;color:#6b7289">{Encode(expiry)} {Encode(footnote)}</p>
            <p style="margin:0;font-size:12px;color:#6b7289;word-break:break-all">Si el botón no funciona, copia este enlace: {Encode(link)}</p>
            """);
        var text = $"{intro}\n\n{action}: {link}\n\n{expiry} {footnote}";
        return new EmailMessage([to], subject, html, text);
    }

    private static string Layout(string color, string title, string content) => $"""
        <!DOCTYPE html>
        <html lang="es"><body style="margin:0;padding:24px;background:#f5f6fa;font-family:Segoe UI,Roboto,Helvetica,Arial,sans-serif">
          <table role="presentation" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #e2e5ee">
            <tr><td style="height:5px;background:{color}"></td></tr>
            <tr><td style="padding:24px 28px 8px">
              <p style="margin:0 0 4px;font-size:12px;font-weight:700;color:#5b5bd6;letter-spacing:.04em">BACKUPHUB</p>
              <h1 style="margin:0;font-size:20px;color:#151827">{Encode(title)}</h1>
            </td></tr>
            <tr><td style="padding:16px 28px 28px">{content}</td></tr>
          </table>
        </body></html>
        """;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
