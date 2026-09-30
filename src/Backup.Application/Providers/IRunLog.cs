namespace Backup.Application.Providers;

/// <summary>Bitácora de una ejecución, visible en la UI.</summary>
public interface IRunLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);

    /// <summary>Registra un elemento que el respaldo no incluye; la ejecución termina con advertencias.</summary>
    void Omit(string message);
}

public sealed class NullRunLog : IRunLog
{
    public static readonly NullRunLog Instance = new();

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Omit(string message) { }
    public void Error(string message) { }
}
