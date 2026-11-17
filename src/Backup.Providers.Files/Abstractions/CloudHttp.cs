using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backup.Providers.Files.Abstractions;

/// <summary>
/// HTTP común a las APIs de archivos en la nube (Google Drive, OneDrive, Dropbox, WebDAV): un cliente compartido,
/// reintentos ante límites de velocidad (429) y errores transitorios (5xx), y mensajes de error legibles.
/// </summary>
internal static class CloudHttp
{
    private const int MaxAttempts = 5;

    /// <summary>Cliente compartido: reutiliza conexiones y renueva DNS cada pocos minutos.</summary>
    public static HttpClient Shared { get; } = Create(acceptAnyCertificate: false);

    public static HttpClient Create(bool acceptAnyCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (acceptAnyCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Backup/1.0");
        return client;
    }

    /// <summary>
    /// Envía la petición que arma <paramref name="build"/> (se vuelve a armar en cada intento) y reintenta ante 429 y 5xx.
    /// Con <paramref name="allowNotFound"/> un 404 se devuelve en lugar de lanzar.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> build,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        bool allowNotFound = false,
        Func<HttpResponseMessage, bool>? accept = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            var response = await client.SendAsync(request, completion, cancellationToken);
            if (response.IsSuccessStatusCode
                || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                || accept?.Invoke(response) == true)
            {
                return response;
            }

            var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                || (int)response.StatusCode >= 500;
            if (transient && attempt < MaxAttempts)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                response.Dispose();
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delay, cancellationToken);
                continue;
            }

            throw await ErrorAsync(response, cancellationToken);
        }
    }

    /// <summary>Excepción con el código HTTP y el mensaje que devolvió el servicio (recortado).</summary>
    public static async Task<Exception> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            var body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Sin cuerpo legible.
            }

            var detail = body.Length > 600 ? body[..600] + "..." : body;
            var message = $"{(int)response.StatusCode} {response.ReasonPhrase} ({response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path)}){(detail.Length > 0 ? ": " + detail : string.Empty)}";
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new UnauthorizedAccessException("Credenciales rechazadas. " + message),
                HttpStatusCode.Forbidden => new UnauthorizedAccessException("Sin permiso. " + message),
                HttpStatusCode.NotFound => new FileNotFoundException("No existe. " + message),
                _ => new HttpRequestException(message, null, response.StatusCode),
            };
        }
    }

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)
                   ?? throw new InvalidDataException("Respuesta vacía del servicio.");
        }
    }

    public static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    /// <summary>Guarda el cuerpo de la respuesta en un archivo local.</summary>
    public static async Task SaveAsync(HttpResponseMessage response, string localFile, CancellationToken cancellationToken)
    {
        using (response)
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    /// <summary>Pide un token OAuth 2.0 (refresh token, client credentials, JWT...): access token y su vigencia.</summary>
    public static async Task<(string Token, TimeSpan Lifetime)> TokenAsync(string tokenEndpoint, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        var fields = form.ToList();
        using var response = await Shared.PostAsync(tokenEndpoint, new FormUrlEncodedContent(fields), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = body;
            try
            {
                var json = JsonNode.Parse(body);
                reason = $"{json?["error"]}: {json?["error_description"] ?? json?["error"]?["message"]}";
            }
            catch (JsonException)
            {
                // Se deja el cuerpo tal cual.
            }

            throw new UnauthorizedAccessException($"No se pudo obtener el token de acceso ({(int)response.StatusCode}). {reason}");
        }

        var result = JsonNode.Parse(body);
        var token = result?["access_token"]?.GetValue<string>()
                    ?? throw new UnauthorizedAccessException("El servicio de autenticación no devolvió un token de acceso.");
        var seconds = result?["expires_in"] is JsonValue value && value.TryGetValue<int>(out var number) ? number
            : int.TryParse(result?["expires_in"]?.ToString(), out var parsed) ? parsed : 3600;
        return (token, TimeSpan.FromSeconds(seconds));
    }

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    /// <summary>Ruta sin '/' al principio ni al final ("/a/b/" → "a/b"; "/" → "").</summary>
    public static string Trim(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>Divide "a/b/c.txt" en ("a/b", "c.txt").</summary>
    public static (string Directory, string Name) Split(string path)
    {
        var clean = Trim(path);
        var slash = clean.LastIndexOf('/');
        return slash < 0 ? (string.Empty, clean) : (clean[..slash], clean[(slash + 1)..]);
    }

    /// <summary>Cada segmento codificado para URL, unidos con '/'.</summary>
    public static string EncodePath(string path) =>
        string.Join('/', Trim(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}

/// <summary>Token de acceso que se renueva solo antes de vencer (un respaldo grande puede durar más que el token).</summary>
internal sealed class AccessToken(Func<CancellationToken, Task<(string Token, TimeSpan Lifetime)>> fetch)
{
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_token is null || DateTimeOffset.UtcNow >= _expires - TimeSpan.FromMinutes(5))
        {
            var (token, lifetime) = await fetch(cancellationToken);
            _token = token;
            _expires = DateTimeOffset.UtcNow + lifetime;
        }

        return _token;
    }

    /// <summary>Envía con "Authorization: Bearer" (ver <see cref="CloudHttp.SendAsync"/>).</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> build,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        bool allowNotFound = false,
        Func<HttpResponseMessage, bool>? accept = null)
    {
        var token = await GetAsync(cancellationToken);
        return await CloudHttp.SendAsync(client, () =>
        {
            var request = build();
            request.Headers.Authorization = CloudHttp.Bearer(token);
            return request;
        }, cancellationToken, completion, allowNotFound, accept);
    }
}
