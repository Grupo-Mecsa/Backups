using System.Net.Http.Json;
using System.Text.Json;
using Backup.Application.WhatsApp;
using Microsoft.Extensions.Options;

namespace Backup.Infrastructure.WhatsApp;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>URL de WAHA de la plataforma (opcional: cada tenant puede configurar la suya en Alertas).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Valor de <c>WAHA_API_KEY</c> de la instancia (cabecera X-Api-Key).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Sesión de WAHA desde la que se envían las alertas.</summary>
    public string Session { get; set; } = "default";
}

/// <summary>
/// Cliente de WAHA (WhatsApp HTTP API). La API key viaja en una cabecera y el cliente HTTP se registra sin loggers
/// (ver <see cref="DependencyInjection"/>), así que no aparece en los logs.
/// </summary>
public sealed class WahaClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<WhatsAppOptions> options) : IWhatsAppSender
{
    public const string HttpClientName = "waha";

    public WhatsAppConnection? Platform => options.CurrentValue is { BaseUrl: { Length: > 0 } url } platform
        ? new WhatsAppConnection(url, platform.ApiKey, platform.Session)
        : null;

    public async Task SendTextAsync(WhatsAppConnection connection, string chatId, string text, CancellationToken cancellationToken)
    {
        using var request = Request(connection, HttpMethod.Post, "api/sendText");
        request.Content = JsonContent.Create(new { session = connection.Session, chatId, text, linkPreview = false });
        using var response = await Client().SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<WhatsAppChat>> ListGroupsAsync(WhatsAppConnection connection, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(connection, $"api/{Uri.EscapeDataString(connection.Session)}/groups?limit=500", cancellationToken);

        // Cada motor de WAHA devuelve los grupos con otra forma: GOWS {JID, Name}, NOWEB/WEBJS {id, subject|name}.
        IEnumerable<JsonElement> items = json.RootElement.ValueKind switch
        {
            JsonValueKind.Array => json.RootElement.EnumerateArray(),
            JsonValueKind.Object => json.RootElement.EnumerateObject().Select(p => p.Value),
            _ => [],
        };

        return [.. items
            .Select(g => (Id: IdOf(g), Name: Text(g, "Name") ?? Text(g, "subject") ?? Text(g, "name")))
            .Where(g => g.Id is not null && g.Id.EndsWith("@g.us", StringComparison.Ordinal))
            .Select(g => new WhatsAppChat(g.Id!, string.IsNullOrWhiteSpace(g.Name) ? g.Id! : g.Name!))
            .DistinctBy(g => g.Id)];
    }

    public async Task<IReadOnlyList<WhatsAppSession>> ListSessionsAsync(WhatsAppConnection connection, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(connection, "api/sessions?all=true", cancellationToken);
        return json.RootElement.ValueKind != JsonValueKind.Array
            ? []
            : [.. json.RootElement.EnumerateArray()
                .Where(s => Text(s, "name") is not null)
                .Select(s => new WhatsAppSession(
                    Text(s, "name")!,
                    Text(s, "status") ?? "UNKNOWN",
                    s.TryGetProperty("me", out var me) && me.ValueKind == JsonValueKind.Object ? Text(me, "id") : null))];
    }

    private async Task<JsonDocument> GetJsonAsync(WhatsAppConnection connection, string path, CancellationToken cancellationToken)
    {
        using var request = Request(connection, HttpMethod.Get, path);
        using var response = await Client().SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    private static HttpRequestMessage Request(WhatsAppConnection connection, HttpMethod method, string path)
    {
        if (!Uri.TryCreate(connection.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"La URL de WAHA no es válida: {connection.BaseUrl}");
        }

        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        if (!string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            request.Headers.Add("X-Api-Key", connection.ApiKey);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = body.Length > 300 ? body[..300] + "…" : body;
        throw new HttpRequestException((int)response.StatusCode switch
        {
            401 or 403 => "WAHA rechazó la API key: revisa que sea el WAHA_API_KEY de la instancia.",
            404 => $"WAHA no encontró la sesión o el chat ({detail}).",
            422 => $"La sesión de WhatsApp no está lista; revísala en el panel de WAHA ({detail}).",
            _ => $"WAHA respondió {(int)response.StatusCode}: {detail}",
        }, null, response.StatusCode);
    }

    private static string? IdOf(JsonElement group)
    {
        if (Text(group, "JID") is { } jid)
        {
            return jid;
        }

        if (group.TryGetProperty("id", out var id))
        {
            return id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Object ? Text(id, "_serialized") : null;
        }

        return null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
