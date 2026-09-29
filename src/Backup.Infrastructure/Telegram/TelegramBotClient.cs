using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backup.Application.Telegram;
using Microsoft.Extensions.Options;

namespace Backup.Infrastructure.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>Token de @BotFather. Vacío = Telegram deshabilitado.</summary>
    public string? BotToken { get; set; }

    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
}

/// <summary>
/// Cliente de la Bot API sobre HttpClient. El token viaja en la ruta de la URL, así que el cliente
/// HTTP se registra sin loggers (ver <see cref="DependencyInjection"/>) para que no aparezca en los logs.
/// </summary>
public sealed class TelegramBotClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<TelegramOptions> options) : ITelegramBot
{
    public const string HttpClientName = "Telegram";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string? _username;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.BotToken);

    public string? Username => _username;

    public async Task<string> GetMeAsync(CancellationToken cancellationToken)
    {
        var me = await CallAsync<TgUser>("getMe", new { }, cancellationToken);
        _username = me.Username;
        return me.Username ?? string.Empty;
    }

    public async Task SendAsync(long chatId, string html, CancellationToken cancellationToken)
    {
        var request = new { ChatId = chatId, Text = html, ParseMode = "HTML", LinkPreviewOptions = new { IsDisabled = true } };
        try
        {
            await CallAsync<JsonElement>("sendMessage", request, cancellationToken);
        }
        catch (TelegramApiException ex) when (ex.RetryAfter is { } wait && wait <= 30)
        {
            // Límite de mensajes: Telegram indica cuánto esperar.
            await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
            await CallAsync<JsonElement>("sendMessage", request, cancellationToken);
        }
        catch (TelegramApiException ex) when (ex.ErrorCode == 403 || ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new TelegramChatUnavailableException(ex.Message);
        }
    }

    /// <summary>Long polling: espera hasta <paramref name="timeoutSeconds"/> a que lleguen mensajes.</summary>
    public Task<IReadOnlyList<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<TgUpdate>>("getUpdates", new
        {
            Offset = offset,
            Timeout = timeoutSeconds,
            AllowedUpdates = new[] { "message", "my_chat_member" },
        }, cancellationToken);

    private async Task<T> CallAsync<T>(string method, object payload, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.BotToken))
        {
            throw new InvalidOperationException("No hay un bot de Telegram configurado (Telegram__BotToken).");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var uri = new Uri($"{settings.ApiBaseUrl.TrimEnd('/')}/bot{settings.BotToken.Trim()}/{method}");
        using var response = await client.PostAsJsonAsync(uri, payload, Json, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<TgResponse<T>>(Json, cancellationToken)
            ?? throw new TelegramApiException((int)response.StatusCode, $"Respuesta vacía de Telegram ({(int)response.StatusCode}).", null);

        if (!body.Ok || body.Result is null)
        {
            var description = body.Description ?? response.ReasonPhrase ?? "Error desconocido";
            if (body.ErrorCode == 401)
            {
                description = "El token del bot no es válido (revisa Telegram__BotToken).";
            }

            throw new TelegramApiException(body.ErrorCode ?? (int)response.StatusCode, description, body.Parameters?.RetryAfter);
        }

        return body.Result;
    }
}

public sealed class TelegramApiException(int errorCode, string message, int? retryAfter) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
    public int? RetryAfter { get; } = retryAfter;
}

// ---- Tipos de la Bot API (solo los campos usados) ----

public sealed record TgResponse<T>(bool Ok, T? Result, string? Description, int? ErrorCode, TgResponseParameters? Parameters);

public sealed record TgResponseParameters(int? RetryAfter);

public sealed record TgUpdate(long UpdateId, TgMessage? Message, TgChatMemberUpdated? MyChatMember);

public sealed record TgMessage(TgChat Chat, TgUser? From, string? Text);

public sealed record TgChat(long Id, string Type, string? Title, string? Username, string? FirstName, string? LastName);

public sealed record TgUser(long Id, string? Username, string? FirstName);

public sealed record TgChatMemberUpdated(TgChat Chat, TgChatMember NewChatMember);

public sealed record TgChatMember(string Status);
