using System.Globalization;
using System.Security.Claims;
using System.Text;
using Backup.Application;
using Backup.Application.Abstractions;
using Backup.Application.Pipeline;
using Backup.Application.Runs;
using Backup.Application.Security;
using Backup.Domain.Runs;
using Backup.Infrastructure.Identity;

namespace Backup.Web.Services;

/// <summary>Descargas relacionadas con las ejecuciones (fuera de Blazor, para poder devolver archivos).</summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/runs/{id:guid}/log", async (Guid id, HttpContext http, IRunRepository runs, CancellationToken cancellationToken) =>
        {
            var run = await runs.GetAsync(id, cancellationToken);
            // Mismo aislamiento que la UI: solo ejecuciones del tenant activo del usuario.
            if (run is null || !Guid.TryParse(http.User.FindFirstValue(AppClaims.TenantId), out var tenantId) || run.TenantId != tenantId)
            {
                return Results.NotFound();
            }

            var stamp = Display.ToLocal(run.StartedAt).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var fileName = $"{ArtifactNaming.Folder(run.JobName)}_{stamp}.log";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Render(run))).ToArray(), "text/plain; charset=utf-8", fileName);
        }).RequireAuthorization();

        // Descargar el respaldo: tal cual está en el destino, o descifrado y descomprimido (?decoded=true).
        endpoints.MapGet("/runs/{id:guid}/artifact", async (Guid id, bool? decoded, HttpContext http, ArtifactService artifacts, CancellationToken cancellationToken) =>
            await ServeAsync(http, tenantId => artifacts.PrepareAsync(id, tenantId, decoded ?? false, cancellationToken)))
            .RequireAuthorization(AppPolicies.CanManage);

        // Un archivo suelto de un respaldo .zip.
        endpoints.MapGet("/runs/{id:guid}/artifact/entry", async (Guid id, string path, HttpContext http, ArtifactService artifacts, CancellationToken cancellationToken) =>
            await ServeAsync(http, tenantId => artifacts.ExtractEntryAsync(id, tenantId, path, cancellationToken)))
            .RequireAuthorization(AppPolicies.CanManage);

        return endpoints;
    }

    private static async Task<IResult> ServeAsync(HttpContext http, Func<Guid, Task<PreparedArtifact>> prepare)
    {
        // Mismo aislamiento que la UI: solo ejecuciones del tenant activo del usuario.
        if (!Guid.TryParse(http.User.FindFirstValue(AppClaims.TenantId), out var tenantId))
        {
            return Results.NotFound();
        }

        try
        {
            var file = await prepare(tenantId);
            return Results.File(file.FilePath, "application/octet-stream", file.DownloadName, enableRangeProcessing: true);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static string Render(BackupRun run)
    {
        var text = new StringBuilder();
        text.AppendLine("BackupHub — bitácora de ejecución");
        text.AppendLine(new string('=', 60));
        Field(text, "Trabajo", run.JobName);
        Field(text, "Ejecución", run.Id.ToString());
        Field(text, "Disparador", run.Trigger == RunTrigger.Scheduled ? "Programado" : "Manual");
        Field(text, "Estado", run.Status switch
        {
            RunStatus.Running => "En curso",
            RunStatus.Succeeded => "Correcto",
            RunStatus.Failed => "Falló",
            RunStatus.Warning => "Con advertencias",
            _ => "Cancelado",
        });
        Field(text, "Inicio", Display.DateTime(run.StartedAt));
        Field(text, "Fin", Display.DateTime(run.FinishedAt));
        Field(text, "Duración", Display.Duration(run.Duration));
        Field(text, "Archivo", run.ArtifactName ?? "—");
        Field(text, "Tamaño", ByteSize.Format(run.SizeBytes));
        Field(text, "SHA-256", run.Sha256 ?? "—");
        Field(text, "Retención", $"{run.DeletedByRetention} eliminados");
        if (run.Error is not null)
        {
            Field(text, "Error", run.Error);
        }

        text.AppendLine(new string('=', 60));
        text.AppendLine();
        text.Append(run.Log);
        return text.ToString();
    }

    private static void Field(StringBuilder text, string label, string value) =>
        text.Append((label + ":").PadRight(12)).AppendLine(value);
}
