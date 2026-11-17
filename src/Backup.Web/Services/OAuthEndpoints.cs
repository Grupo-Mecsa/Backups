using System.Net;
using System.Security.Claims;
using Backup.Application.Security;

namespace Backup.Web.Services;

/// <summary>Callback de OAuth: el servicio (Google, Microsoft, Dropbox) vuelve aquí, en la ventana emergente, tras autorizar.</summary>
public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/" + OAuthFlows.CallbackPath, async (
            string? state, string? code, string? error, string? error_description,
            HttpContext http, OAuthFlows flows, CancellationToken cancellationToken) =>
        {
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            try
            {
                var result = await flows.CompleteAsync(state, userId, code, error, error_description, cancellationToken);
                return Page(true, result.Account is null ? "Cuenta conectada." : $"Conectado como {result.Account}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or HttpRequestException)
            {
                return Page(false, ex.Message);
            }
        }).RequireAuthorization(AppPolicies.CanManage);

        return endpoints;
    }

    /// <summary>Página mínima de la ventana emergente: avisa y se cierra sola si salió bien (el formulario ya se actualizó).</summary>
    private static IResult Page(bool success, string message)
    {
        var text = WebUtility.HtmlEncode(message);
        var html = $$"""
            <!doctype html>
            <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{(success ? "Cuenta conectada" : "No se pudo conectar")}}</title>
            <style>
              :root { color-scheme: light dark; }
              body { font-family: system-ui, sans-serif; display: grid; place-items: center; min-height: 100vh; margin: 0; padding: 16px; box-sizing: border-box; }
              main { max-width: 420px; text-align: center; }
              h1 { font-size: 20px; margin: 0 0 8px; color: {{(success ? "#16a34a" : "#dc2626")}}; }
              p { margin: 0 0 16px; line-height: 1.5; }
              button { font: inherit; padding: 8px 16px; border-radius: 8px; border: 1px solid #8884; background: transparent; cursor: pointer; }
            </style></head>
            <body><main>
              <h1>{{(success ? "✓ Cuenta conectada" : "No se pudo conectar")}}</h1>
              <p>{{text}}</p>
              <p>{{(success ? "Vuelve a BackupHub y guarda la conexión. Esta ventana se cerrará sola." : "Cierra esta ventana e inténtalo de nuevo desde BackupHub.")}}</p>
              <button type="button" onclick="window.close()">Cerrar ventana</button>
            </main>
            {{(success ? "<script>setTimeout(function () { window.close(); }, 1800);</script>" : string.Empty)}}
            </body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
