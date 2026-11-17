namespace Backup.Application.Providers;

/// <summary>
/// Capacidad opcional: proveedores cuyas credenciales se obtienen autorizando una cuenta con OAuth 2.0 (Google Drive,
/// OneDrive, Dropbox). La web ofrece un botón «Conectar con …» que abre el consentimiento del servicio y guarda el
/// refresh token en el campo indicado, sin que el usuario tenga que generarlo a mano.
/// </summary>
public interface IOAuthConnectable
{
    OAuthSpec OAuth { get; }
}

/// <summary>Cómo autorizar una cuenta con el flujo de código de autorización (con PKCE).</summary>
/// <param name="Name">Nombre del servicio para la UI: «Google», «Microsoft»...</param>
/// <param name="ConfigSection">
/// Sección de configuración con un cliente OAuth predeterminado: <c>Backup:OAuth:{ConfigSection}:ClientId</c> y
/// <c>ClientSecret</c>. Así el administrador registra la aplicación una sola vez para todas las conexiones.
/// </param>
/// <param name="ClientIdKey">Campo con el Client ID (vacío = el de la configuración).</param>
/// <param name="ClientSecretKey">Campo con el client secret.</param>
/// <param name="RefreshTokenKey">Campo donde se guarda el refresh token obtenido.</param>
public sealed record OAuthSpec(
    string Name,
    string ConfigSection,
    string Scope,
    string ClientIdKey,
    string ClientSecretKey,
    string RefreshTokenKey)
{
    /// <summary>URL de consentimiento (puede depender de la configuración, p. ej. el tenant de Microsoft).</summary>
    public required Func<ProviderSettings, string> AuthorizeUrl { get; init; }

    public required Func<ProviderSettings, string> TokenUrl { get; init; }

    /// <summary>Parámetros adicionales de la URL de consentimiento (p. ej. access_type=offline en Google).</summary>
    public IReadOnlyDictionary<string, string> ExtraParameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Si la configuración actual usa OAuth (p. ej. Google Drive en modo «OAuth» y no cuenta de servicio).</summary>
    public Func<ProviderSettings, bool> AppliesTo { get; init; } = _ => true;

    /// <summary>Dónde se crea el cliente OAuth, para la ayuda de la UI.</summary>
    public string? SetupHelp { get; init; }
}
