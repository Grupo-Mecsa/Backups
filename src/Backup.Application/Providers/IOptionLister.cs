namespace Backup.Application.Providers;

/// <summary>Si un campo ofrece elegir su valor de una lista consultada al servidor, y cuántos valores admite.</summary>
public enum ListMode
{
    None,

    /// <summary>Un valor (p. ej. la base de datos).</summary>
    Single,

    /// <summary>Varios valores separados por coma (p. ej. esquemas). Vacío = todos.</summary>
    Multiple,
}

/// <summary>
/// Proveedor que puede consultar los valores posibles de sus campos listables (<see cref="SettingField.List"/>):
/// bases de datos, esquemas... La UI los muestra para elegirlos en lugar de escribirlos.
/// </summary>
public interface IOptionLister
{
    Task<IReadOnlyList<string>> ListOptionsAsync(string fieldKey, ProviderSettings settings, CancellationToken cancellationToken);
}
