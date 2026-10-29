namespace Kouki.Kagedama.Bot;

/// <summary>
/// Итог запроса обновлений. Ошибки Telegram — это значения, а не исключения:
/// 429 и 409 требуют разной реакции, и по типу исключения их не различить.
/// </summary>
/// <param name="Ok">Запрос удался.</param>
/// <param name="StatusCode">HTTP-код ответа Bot API.</param>
/// <param name="Updates">Разобранные сообщения (пусто при ошибке).</param>
/// <param name="Error">Описание ошибки от Telegram.</param>
/// <param name="RetryAfter">Сколько ждать, если Telegram просит подождать (429).</param>
public sealed record TelegramUpdatesResult(
    bool Ok,
    int StatusCode,
    IReadOnlyList<TelegramMessage> Updates,
    string? Error = null,
    TimeSpan? RetryAfter = null)
{
    /// <summary>Пустой успешный ответ: новых сообщений нет.</summary>
    public static TelegramUpdatesResult Empty { get; } = new(true, 200, Array.Empty<TelegramMessage>());

    /// <summary>409: работает второй экземпляр бота или установлен webhook.</summary>
    public bool IsConflict => StatusCode == 409;

    /// <summary>401/403: токен неверный или бот заблокирован.</summary>
    public bool IsUnauthorized => StatusCode is 401 or 403;

    /// <summary>429: слишком много запросов, надо ждать.</summary>
    public bool IsRateLimited => StatusCode == 429;
}

/// <summary>Итог отправки сообщения.</summary>
/// <param name="Ok">Отправлено.</param>
/// <param name="StatusCode">HTTP-код.</param>
/// <param name="Error">Описание ошибки.</param>
/// <param name="RetryAfter">Пауза, которую просит Telegram (429).</param>
public sealed record TelegramSendResult(bool Ok, int StatusCode, string? Error = null, TimeSpan? RetryAfter = null)
{
    /// <summary>429: сообщение не отправлено, надо подождать.</summary>
    public bool IsRateLimited => StatusCode == 429;
}

/// <summary>Состояние webhook бота: по нему различается причина 409.</summary>
/// <param name="Ok">Запрос удался.</param>
/// <param name="Url">Адрес webhook; пусто, если он не установлен.</param>
/// <param name="LastError">Последняя ошибка доставки webhook.</param>
/// <param name="PendingUpdateCount">Сколько обновлений ждёт доставки.</param>
public sealed record TelegramWebhookInfo(bool Ok, string? Url, string? LastError = null, int PendingUpdateCount = 0)
{
    /// <summary>Webhook установлен, значит getUpdates работать не будет.</summary>
    public bool HasWebhook => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>Кнопка под сообщением: нажатие приходит как <c>callback_query</c> с тем же кодом.</summary>
/// <param name="Text">Подпись кнопки.</param>
/// <param name="CallbackData">Код, который вернётся в <see cref="TelegramMessage.CallbackData"/>.</param>
public sealed record InlineButton(string Text, string CallbackData);

/// <summary>
/// Клавиатура под сообщением. Строки — ряды кнопок: Telegram рисует их как есть,
/// поэтому «Готово» для каждой задачи ставится в свой ряд.
/// </summary>
public sealed record InlineKeyboard(IReadOnlyList<IReadOnlyList<InlineButton>> Rows)
{
    /// <summary>Одна кнопка в одном ряду.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="callbackData">Код нажатия.</param>
    public static InlineKeyboard One(string text, string callbackData)
        => new(new[] { (IReadOnlyList<InlineButton>)new[] { new InlineButton(text, callbackData) } });

    /// <summary>Кнопки по одной в ряд.</summary>
    /// <param name="buttons">Кнопки в порядке показа.</param>
    public static InlineKeyboard Column(params InlineButton[] buttons)
        => new(buttons.Select(b => (IReadOnlyList<InlineButton>)new[] { b }).ToArray());

    /// <summary>Кнопки в один ряд.</summary>
    /// <param name="buttons">Кнопки в порядке показа.</param>
    public static InlineKeyboard Row(params InlineButton[] buttons)
        => new(new[] { (IReadOnlyList<InlineButton>)buttons });
}

/// <summary>Что бот ждёт от Telegram (позволяет подменять транспорт в тестах).</summary>
public interface ITelegramApi
{
    /// <summary>Забирает обновления длинным опросом.</summary>
    Task<TelegramUpdatesResult> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken = default);

    /// <summary>Отправляет текст в чат, при необходимости с клавиатурой под ним.</summary>
    /// <param name="chatId">Чат.</param>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="keyboard">Клавиатура или <c>null</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TelegramSendResult> SendMessageAsync(
        long chatId,
        string text,
        InlineKeyboard? keyboard = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Подтверждает нажатие кнопки: без этого Telegram держит «часики» на кнопке,
    /// сколько бы бот ни считал.
    /// </summary>
    /// <param name="callbackQueryId">Идентификатор нажатия.</param>
    /// <param name="text">Короткая подпись «сделано» (необязательно).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TelegramSendResult> AnswerCallbackAsync(
        string callbackQueryId,
        string? text = null,
        CancellationToken cancellationToken = default);

    /// <summary>Спрашивает, установлен ли webhook.</summary>
    Task<TelegramWebhookInfo> GetWebhookInfoAsync(CancellationToken cancellationToken = default);
}
