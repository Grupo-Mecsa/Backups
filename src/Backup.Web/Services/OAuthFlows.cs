using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backup.Application.Providers;
using Microsoft.AspNetCore.WebUtilities;

namespace Backup.Web.Services;

/// <summary>Cuenta autorizada: lo que se escribe en el formulario del proveedor.</summary>
/// <param name="Settings">Valores a guardar (refresh token y, si se usó el cliente predeterminado, su ID y secreto).</param>
/// <param name="Account">Correo o nombre de la cuenta autorizada, si el servicio lo informa.</param>
public sealed record OAuthResult(IReadOnlyDictionary<string, string> Settings, string? Account);

/// <summary>Autorización en curso: se crea al pulsar «Conectar» y se completa en el callback (o pegando la URL).</summary>
public sealed class OAuthFlow
{
    internal OAuthFlow(string state, string userId, OAuthSpec spec, ProviderSettings settings, string clientId, string? clientSecret,
        bool usesDefaultClient, string redirectUri, string verifier)
    {
        State = state;
        UserId = userId;
        Spec = spec;
        Settings = settings;
        ClientId = clientId;
        ClientSecret = clientSecret;
        UsesDefaultClient = usesDefaultClient;
        RedirectUri = redirectUri;
        Verifier = verifier;
    }

    public string State { get; }
    public string RedirectUri { get; }
    internal string UserId { get; }
    internal OAuthSpec Spec { get; }
    internal ProviderSettings Settings { get; }
    internal string ClientId { get; }
    internal string? ClientSecret { get; }
    internal bool UsesDefaultClient { get; }
    internal string Verifier { get; }
    internal DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(15);
    internal TaskCompletionSource<OAuthResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>URL de consentimiento del servicio.</summary>
    public string AuthorizeUrl
    {
        get
        {
            var query = new Dictionary<string, string?>
            {
                ["client_id"] = ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["state"] = State,
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))),
                ["code_challenge_method"] = "S256",
            };
            if (Spec.Scope.Length > 0)
            {
                query["scope"] = Spec.Scope;
            }

            foreach (var (key, value) in Spec.ExtraParameters)
            {
                query[key] = value;
            }

            return QueryHelpers.AddQueryString(Spec.AuthorizeUrl(Settings), query);
        }
    }

    /// <summary>Se completa cuando el usuario autoriza (o falla/cancela).</summary>
    public Task<OAuthResult> Result => Completion.Task;

    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Flujo OAuth 2.0 de código de autorización con PKCE para conectar cuentas (Google, Microsoft, Dropbox) desde la web.
/// El código se cambia por el token en el servidor: el refresh token nunca pasa por el navegador.
/// </summary>
public sealed class OAuthFlows(IConfiguration configuration, IHttpClientFactory httpClients, ILogger<OAuthFlows> logger)
{
    /// <summary>Redirección para el modo manual: el navegador no llega a ningún lado y el usuario pega la URL.</summary>
    public const string ManualRedirectUri = "http://localhost";

    public const string CallbackPath = "oauth/callback";

    private readonly ConcurrentDictionary<string, OAuthFlow> _flows = new(StringComparer.Ordinal);

    /// <summary>Cliente predeterminado configurado por el administrador para ese servicio, si hay.</summary>
    public (string? ClientId, string? ClientSecret) DefaultClient(OAuthSpec spec) =>
        (NullIfEmpty(configuration[$"Backup:OAuth:{spec.ConfigSection}:ClientId"]),
         NullIfEmpty(configuration[$"Backup:OAuth:{spec.ConfigSection}:ClientSecret"]));

    /// <summary>URI de redirección que debe registrarse en el servicio: Backup:PublicUrl o la dirección actual.</summary>
    public string CallbackUri(string baseUri)
    {
        var root = configuration["Backup:PublicUrl"] is { Length: > 0 } configured ? configured : baseUri;
        return root.TrimEnd('/') + "/" + CallbackPath;
    }

    /// <summary>
    /// Si los servicios aceptan esa redirección: exigen HTTPS, salvo para localhost. Por IP y http hay que usar el modo
    /// manual (Google además rechaza IPs privadas aunque tengan HTTPS).
    /// </summary>
    public static bool IsRedirectAllowed(string redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);

    /// <param name="settings">Configuración actual del formulario (Client ID propio, tenant...).</param>
    public OAuthFlow Start(OAuthSpec spec, ProviderSettings settings, string userId, string redirectUri)
    {
        Cleanup();
        var ownClient = settings.Get(spec.ClientIdKey);
        var (defaultId, defaultSecret) = DefaultClient(spec);
        var clientId = ownClient ?? defaultId
                       ?? throw new InvalidOperationException(
                           $"Indica el Client ID de tu aplicación de {spec.Name}, o configura Backup__OAuth__{spec.ConfigSection}__ClientId en el servidor.");
        var clientSecret = ownClient is not null ? settings.GetRaw(spec.ClientSecretKey) : defaultSecret;

        var state = OAuthFlow.Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = OAuthFlow.Base64Url(RandomNumberGenerator.GetBytes(48));
        var flow = new OAuthFlow(state, userId, spec, settings, clientId, clientSecret, ownClient is null, redirectUri, verifier);
        _flows[state] = flow;
        return flow;
    }

    public void Cancel(OAuthFlow flow)
    {
        if (_flows.TryRemove(flow.State, out _))
        {
            flow.Completion.TrySetCanceled();
        }
    }

    /// <summary>Respuesta del servicio en el callback (o pegada a mano): cambia el código por el refresh token.</summary>
    /// <returns>Mensaje para el usuario.</returns>
    public async Task<OAuthResult> CompleteAsync(string? state, string userId, string? code, string? error, string? errorDescription,
        CancellationToken cancellationToken)
    {
        if (state is null || !_flows.TryGetValue(state, out var flow) || flow.Expires < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("La autorización caducó o no corresponde a esta sesión. Vuelve a pulsar «Conectar».");
        }

        if (!string.Equals(flow.UserId, userId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("La autorización la inició otro usuario.");
        }

        _flows.TryRemove(state, out _);
        try
        {
            if (error is not null || string.IsNullOrEmpty(code))
            {
                throw new InvalidOperationException(error == "access_denied"
                    ? "Se canceló la autorización."
                    : $"{flow.Spec.Name} rechazó la autorización: {errorDescription ?? error ?? "sin código"}.");
            }

            var result = await ExchangeAsync(flow, code, cancellationToken);
            flow.Completion.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            flow.Completion.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Modo manual: lo que pegó el usuario puede ser la URL completa a la que lo llevó el navegador
    /// (http://localhost/?code=...&amp;state=...) o solo el código.
    /// </summary>
    public Task<OAuthResult> CompleteManuallyAsync(OAuthFlow flow, string pasted, string userId, CancellationToken cancellationToken)
    {
        var text = pasted.Trim();
        var query = text.Contains('?', StringComparison.Ordinal) ? QueryHelpers.ParseQuery(text[text.IndexOf('?', StringComparison.Ordinal)..]) : null;
        if (query is null)
        {
            return CompleteAsync(flow.State, userId, text, null, null, cancellationToken);
        }

        var state = query.TryGetValue("state", out var s) ? s.ToString() : flow.State;
        if (!string.Equals(state, flow.State, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Esa dirección corresponde a otra autorización. Usa la de la última ventana que abriste.");
        }

        return CompleteAsync(flow.State, userId,
            query.TryGetValue("code", out var code) ? code.ToString() : null,
            query.TryGetValue("error", out var error) ? error.ToString() : null,
            query.TryGetValue("error_description", out var description) ? description.ToString() : null,
            cancellationToken);
    }

    private async Task<OAuthResult> ExchangeAsync(OAuthFlow flow, string code, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = flow.RedirectUri,
            ["client_id"] = flow.ClientId,
            ["code_verifier"] = flow.Verifier,
        };
        if (flow.ClientSecret is not null)
        {
            form["client_secret"] = flow.ClientSecret;
        }

        using var http = httpClients.CreateClient("oauth");
        using var response = await http.PostAsync(flow.Spec.TokenUrl(flow.Settings), new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // Se informa abajo con el cuerpo tal cual.
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Intercambio OAuth con {Service} rechazado: {Status} {Body}", flow.Spec.Name, (int)response.StatusCode, body);
            var reason = json?["error_description"]?.ToString() ?? json?["error"]?.ToString() ?? body;
            throw new InvalidOperationException($"{flow.Spec.Name} no entregó el token: {reason}");
        }

        var refreshToken = json?["refresh_token"]?.GetValue<string>()
                           ?? throw new InvalidOperationException(
                               $"{flow.Spec.Name} no entregó un refresh token. Quita el acceso de la app en tu cuenta y vuelve a conectar.");

        var settings = new Dictionary<string, string> { [flow.Spec.RefreshTokenKey] = refreshToken };
        if (flow.UsesDefaultClient)
        {
            // La conexión necesita el cliente para renovar el token al respaldar.
            settings[flow.Spec.ClientIdKey] = flow.ClientId;
            if (flow.ClientSecret is not null)
            {
                settings[flow.Spec.ClientSecretKey] = flow.ClientSecret;
            }
        }

        return new OAuthResult(settings, AccountFromIdToken(json?["id_token"]?.GetValue<string>()));
    }

    /// <summary>
    /// Correo de la cuenta según el id_token. Llegó directo del servicio por HTTPS, así que basta leerlo; solo se usa
    /// para mostrar con qué cuenta quedó conectada.
    /// </summary>
    internal static string? AccountFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            var payload = JsonNode.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            return payload?["email"]?.ToString() ?? payload?["preferred_username"]?.ToString();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private void Cleanup()
    {
        foreach (var (state, flow) in _flows)
        {
            if (flow.Expires < DateTimeOffset.UtcNow && _flows.TryRemove(state, out _))
            {
                flow.Completion.TrySetCanceled();
            }
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
