using System.Globalization;

namespace Backup.Application.Providers;

/// <summary>Acceso tipado y de solo lectura a la configuración de un proveedor.</summary>
public sealed class ProviderSettings
{
    private readonly IReadOnlyDictionary<string, string?> _values;

    public ProviderSettings(IReadOnlyDictionary<string, string?> values) =>
        _values = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException($"Falta el valor de configuración '{key}'.");

    /// <summary>Igual que <see cref="Get(string)"/> pero sin recortar espacios (útil para contraseñas).</summary>
    public string? GetRaw(string key) =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        bool.TryParse(Get(key), out var value) ? value : fallback;

    /// <summary>Lista separada por comas, punto y coma o saltos de línea.</summary>
    public IReadOnlyList<string> GetList(string key) =>
        Get(key)?.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
