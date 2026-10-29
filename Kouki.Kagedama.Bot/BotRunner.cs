namespace Kouki.Kagedama.Bot;
using System.Globalization;

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
        + "Команды: /tasks — открытые задачи, /ask текст — вопрос без угадывания интента,\n"
        + "/new текст — задача без угадывания.\n\n"
        + "Голосовые задачи — через станцию (кнопка «Записать»).";

    /// <summary>Кнопка «Готово» под задачей: закрывает её без текста.</summary>
    private const string DonePrefix = "done:";

    /// <summary>Нажатие «Готово» под напоминанием.</summary>
    private const string ReminderAckPrefix = "rem:ack:";

    /// <summary>Нажатие «+10 минут» под напоминанием.</summary>
    private const string ReminderSnoozePrefix = "rem:snooze:";

    /// <summary>На сколько откладывается напоминание кнопкой.</summary>
    private const int SnoozeMinutes = 10;

    /// <summary>Кнопка «Показать задачи».</summary>
    private const string ListCallback = "list";

    /// <summary>Кнопка «Как задать вопрос».</summary>
    private const string AskCallback = "ask";

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
    /// Последний чат, из которого писали: напоминания доставляются туда же.
    /// Отдельной привязки «напоминание → чат» пока нет, а держать её в памяти
    /// достаточно: бот и так живёт в одном процессе с одним человеком.
    /// </summary>
    private long _lastChatId;

    /// <summary>Чат, уже записанный в файл (чтобы не писать его на каждое сообщение).</summary>
    private long _savedChatId;

    /// <summary>Файл, где запоминается чат: после перезапуска напоминания идут туда же.</summary>
    private readonly string? _chatFile;

    /// <summary>
    /// Создаёт цикл бота.
    /// </summary>
    /// <param name="telegram">Клиент Telegram.</param>
    /// <param name="kouki">Клиент сервера Kouki.</param>
    /// <param name="options">Настройки.</param>
    /// <param name="log">Куда писать диагностику.</param>
    /// <param name="delay">Пауза (подменяется в тестах, чтобы не ждать секундами).</param>
    /// <param name="chatFile">Файл, где запоминается чат (пусто — только в памяти).</param>
    public BotRunner(
        ITelegramApi telegram,
        IKoukiIntake kouki,
        BotOptions options,
        Action<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? chatFile = null)
    {
        _telegram = telegram ?? throw new ArgumentNullException(nameof(telegram));
        _kouki = kouki ?? throw new ArgumentNullException(nameof(kouki));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? Console.WriteLine;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _chatFile = chatFile;
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

        // После перезапуска чат ещё неизвестен, а напоминание может быть уже созревшим.
        // Сначала пробуем вспомнить чат из файла, и лишь потом спрашиваем Telegram.
        RememberSavedChat();

        if (_lastChatId == 0)
        {
            // Спрашиваем у Telegram последнее обновление — только чтобы узнать чат, не сдвигая offset.
            await LearnLastChatAsync(cancellationToken).ConfigureAwait(false);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            // Напоминание важнее следующего long-poll: пока ждём обновления 25 секунд,
            // напоминание уже могло созреть.
            await DeliverRemindersAsync(cancellationToken).ConfigureAwait(false);

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
        if (message.ChatId != 0)
        {
            _lastChatId = message.ChatId;
        RememberChat(message.ChatId);
        }

        if (message.IsCallback)
        {
            await HandleCallbackAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            await TrySendAsync(message.ChatId, "Пришли текстом, что нужно сделать. Голос — через станцию.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string text = message.Text.Trim();

        // Команда — это просьба к боту, а не фраза для разбора: подсказка вместо угадывания интента.
        if (text.StartsWith('/'))
        {
            await HandleCommandAsync(message.ChatId, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool truncated = text.Length > MaxTextLength;
        if (truncated)
        {
            text = text[..MaxTextLength];
        }

        await SubmitAsync(message.ChatId, text, intent: null, truncated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполняет команду вида <c>/ask сколько минут варить яйцо</c>.
    /// </summary>
    /// <param name="chatId">Чат.</param>
    /// <param name="command">Команда целиком, с косой чертой.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task HandleCommandAsync(long chatId, string command, CancellationToken cancellationToken)
    {
        string body = command[1..];
        int space = body.IndexOf(' ');

        string name = (space < 0 ? body : body[..space]).ToLowerInvariant();
        string arguments = space < 0 ? string.Empty : body[(space + 1)..].Trim();

        // Telegram присылает «/ask@имя_бота …» в группах: отбрасываем хвост адресата.
        int at = name.IndexOf('@');
        if (at > 0)
        {
            name = name[..at];
        }

        switch (name)
        {
            case "start":
            case "help":
                await TrySendAsync(chatId, HelpText, cancellationToken, HelpKeyboard()).ConfigureAwait(false);
                return;

            case "tasks":
            case "list":
            case "список":
                await SendTasksAsync(chatId, cancellationToken).ConfigureAwait(false);
                return;

            case "ask":
            case "вопрос":
                if (arguments.Length == 0)
                {
                    await TrySendAsync(chatId, "Напиши вопрос после команды: /ask сколько минут варить яйцо", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await SubmitAsync(chatId, Clip(arguments), "ask", arguments.Length > MaxTextLength, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case "new":
            case "add":
            case "задача":
                if (arguments.Length == 0)
                {
                    await TrySendAsync(chatId, "Напиши задачу после команды: /new купить хлеб", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await SubmitAsync(chatId, Clip(arguments), "add_task", arguments.Length > MaxTextLength, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case "done":
                if (!Guid.TryParse(arguments, out Guid taskId))
                {
                    await SendTasksAsync(chatId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await CompleteTaskAsync(chatId, taskId, cancellationToken).ConfigureAwait(false);
                return;

            default:
                await TrySendAsync(chatId, "Такой команды нет.\n\n" + HelpText, cancellationToken, HelpKeyboard())
                    .ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Обрабатывает нажатие кнопки под сообщением.</summary>
    /// <param name="message">Обновление с <c>callback_query</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task HandleCallbackAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        string data = message.CallbackData ?? string.Empty;

        if (data == ListCallback)
        {
            await AnswerCallbackAsync(message.CallbackQueryId, null, cancellationToken).ConfigureAwait(false);
            await SendTasksAsync(message.ChatId, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (data == AskCallback)
        {
            await AnswerCallbackAsync(message.CallbackQueryId, null, cancellationToken).ConfigureAwait(false);
            await TrySendAsync(message.ChatId, "Напиши вопрос после /ask — например: /ask почему небо синее", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (data.StartsWith(ReminderAckPrefix, StringComparison.Ordinal)
            && Guid.TryParse(data[ReminderAckPrefix.Length..], out Guid acknowledged))
        {
            await AcknowledgeReminderAsync(message.ChatId, acknowledged, cancellationToken).ConfigureAwait(false);
            await AnswerCallbackAsync(message.CallbackQueryId, "Отмечено", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (data.StartsWith(ReminderSnoozePrefix, StringComparison.Ordinal)
            && Guid.TryParse(data[ReminderSnoozePrefix.Length..], out Guid snoozed))
        {
            await SnoozeReminderAsync(message.ChatId, snoozed, cancellationToken).ConfigureAwait(false);
            await AnswerCallbackAsync(message.CallbackQueryId, $"+{SnoozeMinutes} минут", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (data.StartsWith(DonePrefix, StringComparison.Ordinal)
            && Guid.TryParse(data[DonePrefix.Length..], out Guid taskId))
        {
            await CompleteTaskAsync(message.ChatId, taskId, cancellationToken).ConfigureAwait(false);
            await AnswerCallbackAsync(message.CallbackQueryId, "Готово", cancellationToken).ConfigureAwait(false);
            return;
        }

        await AnswerCallbackAsync(message.CallbackQueryId, null, cancellationToken).ConfigureAwait(false);
        await TrySendAsync(message.ChatId, HelpText, cancellationToken, HelpKeyboard()).ConfigureAwait(false);
    }

    /// <summary>Отправляет фразу в приём и отвечает по результату.</summary>
    private async Task SubmitAsync(
        long chatId,
        string text,
        string? intent,
        bool truncated,
        CancellationToken cancellationToken)
    {
        IntakeAttempt attempt = await _kouki.SubmitAsync(text, intent, cancellationToken).ConfigureAwait(false);

        string reply;
        InlineKeyboard? keyboard = null;

        if (attempt.Ok && attempt.Response is not null)
        {
            reply = IntakeReplyFormatter.Format(attempt.Response);

            // Задача, которую ещё можно закрыть, получает кнопку: закрывать её
            // текстом «выполнено» — это снова доверять угадыванию интента.
            if (attempt.Response.Task is { } task && task.Status is "new" or "in_progress")
            {
                keyboard = InlineKeyboard.One("Готово", DonePrefix + task.Id.ToString("D"));
            }
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

        await TrySendAsync(chatId, reply, cancellationToken, keyboard).ConfigureAwait(false);
    }

    /// <summary>Показывает открытые задачи с кнопками «Готово».</summary>
    private async Task SendTasksAsync(long chatId, CancellationToken cancellationToken)
    {
        TaskListAttempt attempt = await _kouki.ListOpenTasksAsync(5, cancellationToken).ConfigureAwait(false);

        if (!attempt.Ok)
        {
            _log($"tasks: {attempt.Failure} (HTTP {attempt.StatusCode}): {attempt.Detail}");
            await TrySendAsync(chatId, IntakeReplyFormatter.FormatFailure(attempt.Failure, attempt.StatusCode), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (attempt.Tasks.Count == 0)
        {
            await TrySendAsync(chatId, "Открытых задач нет.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = new System.Text.StringBuilder("Открытые задачи:\n");

        foreach (OpenTaskDto task in attempt.Tasks)
        {
            text.Append("\n• ").Append(task.Title)
                .Append(task.Status == "in_progress" ? " (в работе)" : string.Empty);
        }

        InlineKeyboard keyboard = InlineKeyboard.Column(
            attempt.Tasks
                .Select(t => new InlineButton(Clip(t.Title, 40), DonePrefix + t.Id.ToString("D")))
                .ToArray());

        await TrySendAsync(chatId, text.ToString(), cancellationToken, keyboard).ConfigureAwait(false);
    }

    /// <summary>Закрывает задачу и сообщает результат.</summary>
    private async Task CompleteTaskAsync(long chatId, Guid taskId, CancellationToken cancellationToken)
    {
        TaskCommandAttempt attempt = await _kouki.CompleteTaskAsync(taskId, cancellationToken).ConfigureAwait(false);

        if (attempt.Ok)
        {
            await TrySendAsync(chatId, "Готово: задача закрыта.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _log($"complete: {attempt.Failure} (HTTP {attempt.StatusCode}): {attempt.Detail}");
        string reply = attempt.Failure == IntakeFailure.BadRequest
            ? "Сервер не принял закрытие задачи (возможно, она уже закрыта)."
            : IntakeReplyFormatter.FormatFailure(attempt.Failure, attempt.StatusCode);

        await TrySendAsync(chatId, reply, cancellationToken).ConfigureAwait(false);
    }

    private static InlineKeyboard HelpKeyboard()
        => InlineKeyboard.Row(
            new InlineButton("Задачи", ListCallback),
            new InlineButton("Спросить", AskCallback));

    private static string Clip(string value, int limit = MaxTextLength)
        => value.Length <= limit ? value : value[..limit];

    private async Task AnswerCallbackAsync(string? callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(callbackQueryId))
        {
            return;
        }

        try
        {
            await _telegram.AnswerCallbackAsync(callbackQueryId, text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _log($"answerCallbackQuery: сеть недоступна ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Доставляет созревшие напоминания в последний чат, из которого писали.
    /// Ошибки только логируются: напоминание — не повод останавливать бота.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task DeliverRemindersAsync(CancellationToken cancellationToken)
    {
        if (_lastChatId == 0)
        {
            return;
        }

        DueReminderAttempt due;
        try
        {
            due = await _kouki.TakeDueRemindersAsync(5, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _log($"напоминания: сеть недоступна ({ex.GetType().Name})");
            return;
        }

        if (!due.Ok)
        {
            _log($"напоминания: HTTP {due.StatusCode} ({due.Detail ?? due.Failure.ToString()})");
            return;
        }

        foreach (DueReminderDto reminder in due.Reminders)
        {
            string when = string.IsNullOrWhiteSpace(reminder.DueText) ? string.Empty : $"\n{reminder.DueText}";
            string body = $"⏰ {reminder.Text}{when}";

            InlineKeyboard keyboard = InlineKeyboard.Row(
                new InlineButton("Готово", ReminderAckPrefix + reminder.Id.ToString("D")),
                new InlineButton($"+{SnoozeMinutes} минут", ReminderSnoozePrefix + reminder.Id.ToString("D")));

            await TrySendAsync(_lastChatId, body, cancellationToken, keyboard).ConfigureAwait(false);
        }
    }

    /// <summary>Подтверждает напоминание и говорит об этом в чат.</summary>
    /// <param name="chatId">Чат.</param>
    /// <param name="id">Напоминание.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task AcknowledgeReminderAsync(long chatId, Guid id, CancellationToken cancellationToken)
    {
        TaskCommandAttempt attempt = await _kouki.AcknowledgeReminderAsync(id, cancellationToken).ConfigureAwait(false);

        if (!attempt.Ok)
        {
            _log($"напоминание {id:D}: не подтверждено, HTTP {attempt.StatusCode} ({attempt.Detail ?? "-"})");
            await TrySendAsync(chatId, "Не получилось отметить напоминание — попробуй ещё раз.", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Откладывает напоминание на <see cref="SnoozeMinutes"/> минут.</summary>
    /// <param name="chatId">Чат.</param>
    /// <param name="id">Напоминание.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task SnoozeReminderAsync(long chatId, Guid id, CancellationToken cancellationToken)
    {
        TaskCommandAttempt attempt = await _kouki.SnoozeReminderAsync(id, SnoozeMinutes, cancellationToken).ConfigureAwait(false);

        if (attempt.Ok)
        {
            await TrySendAsync(chatId, $"Хорошо, напомню через {SnoozeMinutes} минут.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _log($"напоминание {id:D}: не отложено, HTTP {attempt.StatusCode} ({attempt.Detail ?? "-"})");
        await TrySendAsync(chatId, "Не получилось отложить напоминание — попробуй ещё раз.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Узнаёт чат по последнему обновлению Telegram (<c>offset = -1</c>), не обрабатывая его.
    /// Нужно только чтобы доставить напоминание сразу после перезапуска бота.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task LearnLastChatAsync(CancellationToken cancellationToken)
    {
        try
        {
            TelegramUpdatesResult last = await _telegram.GetUpdatesAsync(-1, 0, cancellationToken).ConfigureAwait(false);

            if (!last.Ok)
            {
                _log($"getUpdates(-1): HTTP {last.StatusCode} ({last.Error ?? "без описания"}) — чат напоминаний неизвестен");
                return;
            }

            if (last.Updates.Count == 0)
            {
                _log("getUpdates(-1): последних обновлений нет — чат напоминаний узнаю из первого сообщения");
                return;
            }

            if (last.Updates[^1].ChatId != 0)
            {
                _lastChatId = last.Updates[^1].ChatId;
                _log($"напоминания пойдут в чат {_lastChatId}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _log($"getUpdates(-1): сеть недоступна ({ex.GetType().Name}) — чат напоминаний неизвестен");
        }
    }

    /// <summary>Вспоминает чат из файла: после перезапуска напоминания идут в тот же чат.</summary>
    private void RememberSavedChat()
    {
        if (string.IsNullOrWhiteSpace(_chatFile))
        {
            return;
        }

        try
        {
            if (!File.Exists(_chatFile))
            {
                return;
            }

            string raw = File.ReadAllText(_chatFile).Trim();

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long saved) && saved != 0)
            {
                _lastChatId = saved;
                _log($"чат напоминаний из файла: {_lastChatId}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"файл чата недоступен ({ex.GetType().Name}): чат напоминаний узнаю из первого сообщения");
        }
    }

    /// <summary>Запоминает чат: в памяти и в файле (если файл настроен).</summary>
    private void RememberChat(long chatId)
    {
        if (chatId == 0 || chatId == _savedChatId)
        {
            return;
        }

        _savedChatId = chatId;

        if (string.IsNullOrWhiteSpace(_chatFile))
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_chatFile);

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_chatFile, chatId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"не удалось запомнить чат ({ex.GetType().Name}) — напоминания после перезапуска могут не дойти");
        }
    }
    private async Task TrySendAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken,
        InlineKeyboard? keyboard = null)
    {
        try
        {
            TelegramSendResult result = await _telegram.SendMessageAsync(chatId, text, keyboard, cancellationToken).ConfigureAwait(false);

            if (result.Ok)
            {
                return;
            }

            if (result.IsRateLimited)
            {
                TimeSpan wait = result.RetryAfter ?? TimeSpan.FromSeconds(5);
                _log($"sendMessage: 429, повтор через {Format(wait)}");
                await _delay(wait, cancellationToken).ConfigureAwait(false);
                result = await _telegram.SendMessageAsync(chatId, text, keyboard, cancellationToken).ConfigureAwait(false);
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
