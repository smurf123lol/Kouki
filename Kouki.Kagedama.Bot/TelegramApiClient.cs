using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kouki.Kagedama.Bot;

/// <summary>
/// Сообщение из чата Telegram, пригодное для обработки.
/// </summary>
/// <param name="UpdateId">Идентификатор обновления: по нему двигается offset.</param>
/// <param name="ChatId">Идентификатор чата.</param>
/// <param name="Text">Текст сообщения; пусто, если это не текст.</param>
public sealed record TelegramMessage(long UpdateId, long ChatId, string Text);

/// <summary>
/// Минимальный клиент Bot API: только получение обновлений и отправка текста.
/// Внешних пакетов не требует — работает через <see cref="HttpClient"/>.
/// </summary>
public sealed class TelegramApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _botToken;

    /// <summary>
    /// Создаёт клиент.
    /// </summary>
    /// <param name="http">HTTP-клиент.</param>
    /// <param name="botToken">Токен бота.</param>
    /// <param name="baseUrl">Базовый адрес Bot API; по умолчанию — <c>https://api.telegram.org</c>.</param>
    public TelegramApiClient(HttpClient http, string botToken, string? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));

        string normalized = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        _baseUrl = normalized.Length > 0 ? normalized : "https://api.telegram.org";

        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException("Токен бота не задан.", nameof(botToken));
        }

        _botToken = botToken;
    }

    /// <summary>
    /// Забирает новые обновления длинным опросом.
    /// </summary>
    /// <param name="offset">Первый ожидаемый идентификатор обновления.</param>
    /// <param name="timeoutSeconds">Таймаут long polling.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<IReadOnlyList<TelegramMessage>> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        string url = $"{_baseUrl}/bot{_botToken}/getUpdates?timeout={timeoutSeconds}&offset={offset}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return ParseUpdates(document.RootElement);
    }

    /// <summary>
    /// Разбирает ответ Bot API с обновлениями.
    /// </summary>
    /// <param name="root">Корень ответа.</param>
    public static IReadOnlyList<TelegramMessage> ParseUpdates(JsonElement root)
    {
        var messages = new List<TelegramMessage>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("ok", out JsonElement ok)
            || ok.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("result", out JsonElement result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (JsonElement update in result.EnumerateArray())
        {
            if (!update.TryGetProperty("update_id", out JsonElement updateIdElement)
                || !updateIdElement.TryGetInt64(out long updateId)
                || !update.TryGetProperty("message", out JsonElement message))
            {
                continue;
            }

            if (!message.TryGetProperty("chat", out JsonElement chat)
                || !chat.TryGetProperty("id", out JsonElement chatIdElement)
                || !chatIdElement.TryGetInt64(out long chatId))
            {
                continue;
            }

            string text = message.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;

            messages.Add(new TelegramMessage(updateId, chatId, text));
        }

        return messages;
    }

    /// <summary>
    /// Отправляет текстовое сообщение.
    /// </summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="text">Текст.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["disable_web_page_preview"] = true,
        };

        using var content = new StringContent(payload.ToJsonString(PayloadOptions), Encoding.UTF8, "application/json");
        using var response = await _http
            .PostAsync($"{_baseUrl}/bot{_botToken}/sendMessage", content, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }
}
