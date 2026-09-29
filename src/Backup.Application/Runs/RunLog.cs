using System.Globalization;
using System.Text;
using Backup.Application.Providers;
using Microsoft.Extensions.Logging;

namespace Backup.Application.Runs;

/// <summary>Bitácora en memoria de una ejecución que además replica al logger de la aplicación.</summary>
public sealed class RunLog(ILogger logger, string jobName, TimeProvider timeProvider) : IRunLog
{
    private readonly StringBuilder _buffer = new();
    private readonly Lock _gate = new();

    public void Info(string message) => Append("INF", LogLevel.Information, message);
    public void Warn(string message) => Append("WRN", LogLevel.Warning, message);
    public void Error(string message) => Append("ERR", LogLevel.Error, message);

    public override string ToString()
    {
        lock (_gate)
        {
            return _buffer.ToString();
        }
    }

    private void Append(string level, LogLevel logLevel, string message)
    {
        var stamp = timeProvider.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            _buffer.Append(stamp).Append(' ').Append(level).Append(' ').AppendLine(message);
        }

#pragma warning disable CA1848 // El nivel es dinámico
        logger.Log(logLevel, "[{Job}] {Message}", jobName, message);
#pragma warning restore CA1848
    }
}
