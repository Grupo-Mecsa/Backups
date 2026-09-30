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
    private int _omitted;
    private int _version;

    /// <summary>Cambia con cada línea nueva; permite publicar la bitácora solo cuando hay algo nuevo.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Evita que dos publicaciones de la misma ejecución se crucen al guardar.</summary>
    internal SemaphoreSlim PublishGate { get; } = new(1, 1);

    /// <summary>Elementos que quedaron fuera del respaldo.</summary>
    public int Omitted => Volatile.Read(ref _omitted);

    public void Info(string message) => Append("INF", LogLevel.Information, message);
    public void Warn(string message) => Append("WRN", LogLevel.Warning, message);
    public void Error(string message) => Append("ERR", LogLevel.Error, message);

    public void Omit(string message)
    {
        Interlocked.Increment(ref _omitted);
        Warn(message);
    }

    /// <summary>Registra el error y, debajo, el detalle técnico (tipo, causas internas y traza) para analizar el fallo.</summary>
    public void Error(Exception exception)
    {
        Error(exception.Message);
        lock (_gate)
        {
            _buffer.AppendLine("         Detalle técnico:");
            foreach (var line in exception.ToString().Split('\n'))
            {
                _buffer.Append("           ").AppendLine(line.TrimEnd('\r'));
            }
        }
    }

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
            _version++;
        }

#pragma warning disable CA1848 // El nivel es dinámico
        logger.Log(logLevel, "[{Job}] {Message}", jobName, message);
#pragma warning restore CA1848
    }
}
