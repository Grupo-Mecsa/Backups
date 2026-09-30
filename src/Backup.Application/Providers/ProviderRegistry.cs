namespace Backup.Application.Providers;

public interface IProviderRegistry
{
    IReadOnlyList<IBackupSource> Sources { get; }
    IReadOnlyList<IBackupDestination> Destinations { get; }

    IBackupSource GetSource(string key);
    IBackupDestination GetDestination(string key);
    IProvider? Find(ProviderRole role, string key);
}

public static class ProviderRegistryExtensions
{
    /// <summary>Proveedor por clave en cualquier rol: los campos de conexión son los mismos como origen y como destino.</summary>
    public static IProvider? FindAny(this IProviderRegistry registry, string key) =>
        registry.Find(ProviderRole.Source, key) ?? registry.Find(ProviderRole.Destination, key);

    /// <summary>Proveedor que puede recibir restauraciones con esa clave (puede estar registrado como origen o como destino).</summary>
    public static IRestoreTarget? FindRestoreTarget(this IProviderRegistry registry, string key) =>
        registry.Sources.Cast<IProvider>().Concat(registry.Destinations).OfType<IRestoreTarget>()
            .FirstOrDefault(p => string.Equals(p.Descriptor.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Destinos posibles para restaurar un respaldo (ya descifrado y descomprimido) con ese nombre.</summary>
    public static IEnumerable<IRestoreTarget> RestoreTargetsFor(this IProviderRegistry registry, string artifactFileName) =>
        registry.Sources.Cast<IProvider>().Concat(registry.Destinations).OfType<IRestoreTarget>()
            .Where(t => t.CanRestore(artifactFileName))
            .DistinctBy(t => t.Descriptor.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Proveedores cuyos datos de acceso pueden guardarse como conexión (uno por clave).</summary>
    public static IEnumerable<IProvider> ConnectionProviders(this IProviderRegistry registry) =>
        registry.Sources.Cast<IProvider>().Concat(registry.Destinations)
            .Where(p => p.Descriptor.SupportsConnections)
            .DistinctBy(p => p.Descriptor.Key, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Catálogo de proveedores armado por inyección de dependencias: cada módulo registra
/// sus implementaciones y el catálogo las descubre sin conocerlas (DIP/OCP).
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IBackupSource> _sources;
    private readonly Dictionary<string, IBackupDestination> _destinations;

    public ProviderRegistry(IEnumerable<IBackupSource> sources, IEnumerable<IBackupDestination> destinations)
    {
        _sources = sources.ToDictionary(s => s.Descriptor.Key, StringComparer.OrdinalIgnoreCase);
        _destinations = destinations.ToDictionary(d => d.Descriptor.Key, StringComparer.OrdinalIgnoreCase);
        Sources = [.. _sources.Values.OrderBy(s => s.Descriptor.Category).ThenBy(s => s.Descriptor.DisplayName, StringComparer.Ordinal)];
        Destinations = [.. _destinations.Values.OrderBy(d => d.Descriptor.Category).ThenBy(d => d.Descriptor.DisplayName, StringComparer.Ordinal)];
    }

    public IReadOnlyList<IBackupSource> Sources { get; }
    public IReadOnlyList<IBackupDestination> Destinations { get; }

    public IBackupSource GetSource(string key) =>
        _sources.TryGetValue(key, out var source) ? source : throw new KeyNotFoundException($"Origen desconocido: '{key}'.");

    public IBackupDestination GetDestination(string key) =>
        _destinations.TryGetValue(key, out var destination) ? destination : throw new KeyNotFoundException($"Destino desconocido: '{key}'.");

    public IProvider? Find(ProviderRole role, string key) => role switch
    {
        ProviderRole.Source => _sources.GetValueOrDefault(key),
        _ => _destinations.GetValueOrDefault(key),
    };
}
