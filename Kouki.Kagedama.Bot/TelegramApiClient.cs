using System.Globalization;
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
/// Минимальный клиент Bot API: получение обновлений, отправка текста и проверка webhook.
/// Внешних пакетов не требует — работает через <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// Ошибки HTTP возвращаются значениями (<see cref="TelegramUpdatesResult"/> и др.),
/// а не исключениями: вызывающему нужно различать 409 (второй экземпляр/webhook),
/// 429 (пауза по <c>retry_after</c>) и 401 (битый токен) — по типу исключения это
/// неразличимо, а текст исключения нельзя отдавать человеку в чат.
/// </remarks>
public sealed class TelegramApiClient : ITelegramApi
{
    private const int MaxErrorBodyBytes = 8 * 1024;

    /// <summary>
    /// Потолок размера ответа Bot API: сто обновлений — это килобайты, но чужой прокси
    /// может прислать что угодно, поэтому читаем с ограничением.
    /// </summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
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

        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException("Токен бота не задан.", nameof(botToken));
        }

        _botToken = botToken;

        string normalized = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim();
        _baseUri = new Uri(normalized.Length > 0 ? EnsureTrailingSlash(normalized) : "https://api.telegram.org/");
    }

    /// <inheritdoc />
    public async Task<TelegramUpdatesResult> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        string query = string.Create(
            CultureInfo.InvariantCulture,
            $"bot{_botToken}/getUpdates?timeout={timeoutSeconds}&offset={offset}");

        using HttpResponseMessage response = await _http
            .GetAsync(BuildUri(query), cancellationToken)
            .ConfigureAwait(false);

        JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        using (document)
        {
            if (!response.IsSuccessStatusCode || !IsOk(document.RootElement))
            {
                return Failure(response, document.RootElement);
            }

            return new TelegramUpdatesResult(true, (int)response.StatusCode, ParseUpdates(document.RootElement));
        }
    }

    /// <inheritdoc />
    public async Task<TelegramSendResult> SendMessageAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["disable_web_page_preview"] = true,
        };

        using var content = new StringContent(payload.ToJsonString(PayloadOptions), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http
            .PostAsync(BuildUri($"bot{_botToken}/sendMessage"), content, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return new TelegramSendResult(true, (int)response.StatusCode);
        }

        JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        using (document)
        {
            TelegramUpdatesResult failure = Failure(response, document.RootElement);
            return new TelegramSendResult(false, failure.StatusCode, failure.Error, failure.RetryAfter);
        }
    }

    /// <inheritdoc />
    public async Task<TelegramWebhookInfo> GetWebhookInfoAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http
            .GetAsync(BuildUri($"bot{_botToken}/getWebhookInfo"), cancellationToken)
            .ConfigureAwait(false);

        JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        using (document)
        {
            JsonElement root = document.RootElement;
            if (!response.IsSuccessStatusCode || !IsOk(root))
            {
                return new TelegramWebhookInfo(false, null, DescribeError(root));
            }

            if (!root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object)
            {
                return new TelegramWebhookInfo(false, null, "getWebhookInfo: нет поля result");
            }

            string? url = result.TryGetProperty("url", out JsonElement urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;

            string? lastError = result.TryGetProperty("last_error_message", out JsonElement errorElement)
                                && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;

            int pending = result.TryGetProperty("pending_update_count", out JsonElement pendingElement)
                          && pendingElement.TryGetInt32(out int count)
                ? count
                : 0;

            return new TelegramWebhookInfo(true, url, lastError, pending);
        }
    }

    /// <summary>
    /// Разбирает ответ Bot API с обновлениями.
    /// </summary>
    /// <param name="root">Корень ответа.</param>
    public static IReadOnlyList<TelegramMessage> ParseUpdates(JsonElement root)
    {
        var messages = new List<TelegramMessage>();

        if (!IsOk(root)
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
    /// Собирает описание ошибки по телу ответа (описание и код из JSON, если он есть).
    /// </summary>
    /// <param name="root">Корень ответа.</param>
    public static string? DescribeError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("description", out JsonElement description)
            && description.ValueKind == JsonValueKind.String)
        {
            return description.GetString();
        }

        return null;
    }

    private static TelegramUpdatesResult Failure(HttpResponseMessage response, JsonElement root)
    {
        int status = (int)response.StatusCode;

        // Bot API умеет отдавать ok:false и с кодом 200; тогда ориентируемся на error_code.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error_code", out JsonElement codeElement)
            && codeElement.TryGetInt32(out int errorCode)
            && errorCode > 0)
        {
            status = errorCode;
        }

        return new TelegramUpdatesResult(false, status, Array.Empty<TelegramMessage>(), DescribeError(root), RetryAfter(root));
    }

    private static TimeSpan? RetryAfter(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("parameters", out JsonElement parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("retry_after", out JsonElement retry)
            && retry.TryGetInt32(out int seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static bool IsOk(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("ok", out JsonElement ok)
           && ok.ValueKind == JsonValueKind.True;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[MaxErrorBodyBytes];
        int read;

        // Обновления читаем целиком, но с потолком: тело ошибки ограничено ещё сильнее
        // самим обработчиком, а мегабайты от чужого прокси съели бы память.
        while (buffer.Length < MaxResponseBytes
               && (read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            int allowed = (int)Math.Min(read, MaxResponseBytes - buffer.Length);
            await buffer.WriteAsync(chunk.AsMemory(0, allowed), cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("""{"ok":false,"description":"ответ Bot API не разобрать как JSON"}""");
        }
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : value + "/";

    /// <summary>
    /// Склеивает базовый адрес и путь Bot API.
    /// </summary>
    /// <remarks>
    /// Не <c>new Uri(base, relative)</c>: токен вида <c>123456:ABC</c> начинается с двоеточия,
    /// и относительный адрес «bot123456:ABC/getUpdates» разбирается как схема <c>bot123456</c>.
    /// Поэтому собираем абсолютный адрес строкой: базовый адрес уже оканчивается на «/».
    /// </remarks>
    private Uri BuildUri(string relative)
        => new(_baseUri + relative, UriKind.Absolute);
}
