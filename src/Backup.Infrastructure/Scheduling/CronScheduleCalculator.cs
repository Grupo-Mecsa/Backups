using Backup.Application.Abstractions;
using Cronos;

namespace Backup.Infrastructure.Scheduling;

/// <summary>Expresiones cron de 5 campos (o 6 con segundos) y macros como @daily, con zona horaria.</summary>
public sealed class CronScheduleCalculator : IScheduleCalculator
{
    public bool TryValidate(string? expression, string timeZone, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "La expresión cron está vacía.";
            return false;
        }

        if (!TryParse(expression, out _))
        {
            error = "Expresión cron inválida. Usa 5 campos: minuto hora día mes díaSemana (p. ej. \"0 2 * * *\").";
            return false;
        }

        if (!TryFindZone(timeZone, out _))
        {
            error = $"Zona horaria desconocida: {timeZone}.";
            return false;
        }

        return true;
    }

    public DateTimeOffset? GetNextOccurrence(string expression, string timeZone, DateTimeOffset from) =>
        TryParse(expression, out var cron) && TryFindZone(timeZone, out var zone)
            ? cron!.GetNextOccurrence(from, zone!)
            : null;

    private static bool TryParse(string expression, out CronExpression? cron)
    {
        var trimmed = expression.Trim();
        var format = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6
            ? CronFormat.IncludeSeconds
            : CronFormat.Standard;
        return CronExpression.TryParse(trimmed, format, out cron);
    }

    private static bool TryFindZone(string timeZone, out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = null;
            return false;
        }
    }
}
