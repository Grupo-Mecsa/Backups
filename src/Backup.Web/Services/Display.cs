using System.Globalization;
using Backup.Application.Providers;

namespace Backup.Web.Services;

/// <summary>Formato de fechas, duraciones y etiquetas para la UI.</summary>
public static class Display
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es");
    private static TimeZoneInfo _zone = TimeZoneInfo.Local;

    public static void Configure(string? timeZone)
    {
        if (!string.IsNullOrWhiteSpace(timeZone) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            _zone = zone;
        }
    }

    public static DateTimeOffset ToLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _zone);

    public static string DateTime(DateTimeOffset? value) =>
        value is null ? "—" : ToLocal(value.Value).ToString("dd MMM yyyy, HH:mm", Culture);

    public static string Time(DateTimeOffset? value) =>
        value is null ? "—" : ToLocal(value.Value).ToString("HH:mm:ss", Culture);

    public static string Relative(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "nunca";
        }

        var delta = value.Value - DateTimeOffset.UtcNow;
        var future = delta > TimeSpan.Zero;
        var span = delta.Duration();
        var text = span.TotalSeconds < 60 ? "unos segundos"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays} d"
            : DateTime(value);
        return span.TotalDays >= 30 ? text : future ? $"en {text}" : $"hace {text}";
    }

    public static string Duration(TimeSpan? value) => value switch
    {
        null => "—",
        { TotalSeconds: < 1 } => "< 1 s",
        { TotalMinutes: < 1 } v => $"{v.Seconds} s",
        { TotalHours: < 1 } v => $"{(int)v.TotalMinutes} min {v.Seconds} s",
        { } v => $"{(int)v.TotalHours} h {v.Minutes} min",
    };

    public static string Category(ProviderCategory category) => category switch
    {
        ProviderCategory.Database => "Bases de datos",
        ProviderCategory.Cloud => "Nube",
        ProviderCategory.FileTransfer => "Transferencia de archivos",
        _ => "Local",
    };
}
