namespace Kouki.Kagedama.Bot;

/// <summary>
/// Цикл бота: длинный опрос Telegram → приём задач в Kouki → ответ в чат.
/// Ошибки одного сообщения не останавливают бота; 401 и webhook останавливают.
/// </summary>
public sealed class BotRunner
{
    /// <summary>Код выхода: бот остановлен по Ctrl+C или исчерпал ожидание.</summary>
    public const int ExitOk = 0;

    /// <summary>
    /// Код выхода: дальше работать бессмысленно — неверный токен или установленный webhook.
    /// Перезапуск в цикле тут только замаскирует причину.
    /// </summary>
    public const int ExitFatal = 2;

    /// <summary>Сервер приёма не принимает текст длиннее: обрезаем до отправки, а не после отказа.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>
    /// Ответ на команды Telegram (<c>/start</c>, <c>/help</c> и прочие косые черты).
    /// Команды бота — не задачи: они не уходят на сервер и не попадают в журнал.
    /// </summary>
    public const string HelpText =
        "Я принимаю задачи и вопросы обычным текстом.\n\n"
        + "• запиши задачу купить хлеб\n"
        + "• покажи мои задачи\n"
        + "• сколько минут варить пельмени?\n\n"
        + "Голосовые задачи — через станцию (кнопка «Записать»).";

    // Канал с DPI рвёт часть TLS-соединений, поэтому длинная пауза только вредит:
    // сначала несколько быстрых повторов на новом соединении, и лишь потом backoff.
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan QuickRetryDelay = TimeSpan.FromMilliseconds(250);
    private const int QuickRetryAttempts = 3;
    private static readonly TimeSpan ConflictRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ITelegramApi _telegram;
    private readonly IKoukiIntake _kouki;
    private readonly BotOptions _options;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Создаёт цикл бота.
    /// </summary>
    /// <param name="telegram">Клиент Telegram.</param>
    /// <param name="kouki">Клиент сервера Kouki.</param>
    /// <param name="options">Настройки.</param>
    /// <param name="log">Куда писать диагностику.</param>
    /// <param name="delay">Пауза (подменяется в тестах, чтобы не ждать секундами).</param>
    public BotRunner(
        ITelegramApi telegram,
        IKoukiIntake kouki,
        BotOptions options,
        Action<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _telegram = telegram ?? throw new ArgumentNullException(nameof(telegram));
        _kouki = kouki ?? throw new ArgumentNullException(nameof(kouki));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? Console.WriteLine;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    /// <summary>
    /// Работает до отмены или до ошибки, после которой работать бессмысленно.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Код выхода процесса.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        long offset = 0;
        int backoffStep = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TelegramUpdatesResult updates;

            try
            {
                updates = await PollWithQuickRetryAsync(offset, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                TimeSpan wait = Backoff(backoffStep++);
                _log($"getUpdates: сеть недоступна ({ex.GetType().Name}), пауза {Format(wait)}");
                await _delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!updates.Ok)
            {
                int code = await HandlePollingFailureAsync(updates, backoffStep, cancellationToken).ConfigureAwait(false);
                if (code >= 0)
                {
                    return code;
                }

                if (updates.IsConflict || updates.IsRateLimited)
                {
                    // Для этих ошибок пауза уже выдержана внутри; шаг backoff не наращиваем.
                    continue;
                }

                backoffStep++;
                continue;
            }

            backoffStep = 0;

            foreach (TelegramMessage message in updates.Updates)
            {
                offset = Math.Max(offset, message.UpdateId + 1);
                await HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }

        return ExitOk;
    }

    /// <summary>
    /// Возвращает код выхода, если работать дальше нельзя, иначе <c>-1</c>
    /// (пауза уже выдержана внутри).
    /// </summary>
    /// <summary>
    /// Опрашивает Telegram, быстро повторяя оборванные соединения: на канале с DPI часть
    /// TLS-хендшейков обрывается, и ждать секундами вместо повтора бессмысленно.
    /// </summary>
    private async Task<TelegramUpdatesResult> PollWithQuickRetryAsync(long offset, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _telegram
                    .GetUpdatesAsync(offset, _options.PollTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < QuickRetryAttempts
                                       && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _log($"getUpdates: обрыв связи ({ex.GetType().Name}), повтор {attempt}/{QuickRetryAttempts}");
                await _delay(QuickRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> HandlePollingFailureAsync(
        TelegramUpdatesResult updates,
        int backoffStep,
        CancellationToken cancellationToken)
    {
        if (updates.IsConflict)
        {
            TelegramWebhookInfo webhook = await SafeWebhookInfoAsync(cancellationToken).ConfigureAwait(false);

            if (webhook.HasWebhook)
            {
                _log(
                    "Telegram: установлен webhook (" + webhook.Url + "), поэтому getUpdates недоступен. " +
                    "Бот не может работать одновременно с webhook. Удалить: " +
                    "curl -X POST \"https://api.telegram.org/bot<ТОКЕН>/deleteWebhook\"");
                return ExitFatal;
            }

            _log("Telegram 409: похоже, работает второй экземпляр бота — жду и пробую снова");
            await _delay(ConflictRetryDelay, cancellationToken).ConfigureAwait(false);
            return -1;
        }

        if (updates.IsUnauthorized)
        {
            _log(
                $"Telegram не принял токен бота (HTTP {updates.StatusCode}: {updates.Error ?? "без описания"}). " +
                "Проверьте KOUKI_BOT_TOKEN / KoukiBot:BotToken.");
            return ExitFatal;
        }

        if (updates.IsRateLimited)
        {
            TimeSpan wait = updates.RetryAfter ?? TimeSpan.FromSeconds(5);
            _log($"Telegram 429: слишком много запросов, пауза {Format(wait)}");
            await _delay(wait, cancellationToken).ConfigureAwait(false);
            return -1;
        }

        TimeSpan backoff = Backoff(backoffStep);
        _log($"getUpdates: HTTP {updates.StatusCode} ({updates.Error ?? "без описания"}), пауза {Format(backoff)}");
        await _delay(backoff, cancellationToken).ConfigureAwait(false);
        return -1;
    }

    private async Task HandleMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            await TrySendAsync(message.ChatId, "Пришли текстом, что нужно сделать. Голос — через станцию.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string text = message.Text.Trim();

        // /start, /help и любые другие команды — не фразы: отвечаем подсказкой и не создаём задачу.
        if (text.StartsWith('/'))
        {
            await TrySendAsync(message.ChatId, HelpText, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool truncated = text.Length > MaxTextLength;
        if (truncated)
        {
            text = text[..MaxTextLength];
        }

        IntakeAttempt attempt = await _kouki.SubmitAsync(text, cancellationToken).ConfigureAwait(false);

        string reply;
        if (attempt.Ok && attempt.Response is not null)
        {
            reply = IntakeReplyFormatter.Format(attempt.Response);
        }
        else
        {
            // Подробность — в лог, человеку — короткий и понятный текст.
            _log($"intake: {attempt.Failure} (HTTP {attempt.StatusCode}): {attempt.Detail}");
            reply = IntakeReplyFormatter.FormatFailure(attempt.Failure, attempt.StatusCode);
        }

        if (truncated)
        {
            reply += $"\n\n(Текст был длиннее {MaxTextLength} символов — отправил начало.)";
        }

        await TrySendAsync(message.ChatId, reply, cancellationToken).ConfigureAwait(false);
    }

    private async Task TrySendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        try
        {
            TelegramSendResult result = await _telegram.SendMessageAsync(chatId, text, cancellationToken).ConfigureAwait(false);

            if (result.Ok)
            {
                return;
            }

            if (result.IsRateLimited)
            {
                TimeSpan wait = result.RetryAfter ?? TimeSpan.FromSeconds(5);
                _log($"sendMessage: 429, повтор через {Format(wait)}");
                await _delay(wait, cancellationToken).ConfigureAwait(false);
                result = await _telegram.SendMessageAsync(chatId, text, cancellationToken).ConfigureAwait(false);
                if (result.Ok)
                {
                    return;
                }
            }

            _log($"sendMessage: HTTP {result.StatusCode} ({result.Error ?? "без описания"})");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _log($"sendMessage: сеть недоступна ({ex.GetType().Name})");
        }
    }

    private async Task<TelegramWebhookInfo> SafeWebhookInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _telegram.GetWebhookInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _log($"getWebhookInfo: сеть недоступна ({ex.GetType().Name}) — считаю, что webhook нет");
            return new TelegramWebhookInfo(false, null);
        }
    }

    private static TimeSpan Backoff(int step)
    {
        double seconds = Math.Min(MaxBackoff.TotalSeconds, 3 * Math.Pow(2, Math.Min(step, 4)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string Format(TimeSpan span)
        => span.TotalSeconds >= 1 ? $"{span.TotalSeconds:0} с" : $"{span.TotalMilliseconds:0} мс";
}
