using System.Diagnostics;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Data;
using Kouki.Shimi.Context;
using Kouki.Yasabi;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama;

/// <summary>
/// Результат приёма фразы.
/// </summary>
/// <param name="Task">Задача, которую создала исполненная команда; <c>null</c>, если команда задачу не создавала.</param>
/// <param name="Route">Вердикт распознавания.</param>
/// <param name="AutoExecuted">Была ли команда исполнена сразу.</param>
/// <param name="Outcome">Итог обработки фразы: см. <see cref="ActionOutcomes"/>.</param>
/// <param name="ActionId">Запись журнала действий, по которой можно разобрать, что произошло.</param>
public sealed record IntakeResult(TaskRecord? Task, IntentRoute Route, bool AutoExecuted, string Outcome, Guid? ActionId = null);

/// <summary>
/// Точка входа для фраз с телефона, станции, дашборда и агента:
/// текст → интент → команда через диспатчер.
/// </summary>
/// <remarks>
/// Единственное исключение — фраза, которую распознавание не осилило: для неё нет команды,
/// поэтому задача создаётся здесь же со статусом «на разборе». Всё остальное, включая
/// создание задачи (<c>add_task</c>), идёт обычным путём: роутер → валидация → диспатчер → хендлер.
/// Каждый вызов оставляет запись в журнале действий.
/// </remarks>
public sealed class TaskIntakeService
{
    private readonly IIntentClassifier _classifier;
    private readonly IIntentRouter _router;
    private readonly ITaskStore _store;
    private readonly IActionLog _actions;
    private readonly IContextRegistry _contexts;
    private readonly IntakeOptions _options;
    private readonly IClassificationJournal? _journal;
    private readonly IUnitOfWork? _work;

    /// <summary>
    /// Создаёт сервис приёма.
    /// </summary>
    /// <param name="classifier">Классификатор интентов; журнал пишется в нём.</param>
    /// <param name="router">Маршрутизатор по естественному языку.</param>
    /// <param name="store">Хранилище задач: нужно, чтобы вернуть созданную задачу и найти открытую.</param>
    /// <param name="actions">Журнал действий.</param>
    /// <param name="contexts">Реестр контекстов исполнения.</param>
    /// <param name="options">Настройки приёма.</param>
    /// <param name="journal">Журнал распознавания: сюда дописываются источник фразы и данные ASR.</param>
    /// <param name="work">Граница транзакции; без неё каждая запись идёт своим соединением.</param>
    public TaskIntakeService(
        IIntentClassifier classifier,
        IIntentRouter router,
        ITaskStore store,
        IActionLog actions,
        IContextRegistry contexts,
        IntakeOptions options,
        IClassificationJournal journal,
        IUnitOfWork? work = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(options);

        _classifier = classifier;
        _router = router;
        _store = store;
        _actions = actions;
        _contexts = contexts;
        _options = options;
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _work = work;
    }

    /// <summary>
    /// Выполняет записи одной транзакцией, если граница транзакции передана.
    /// </summary>
    private Task<T> InTransactionAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
        => _work is null ? body() : _work.RunAsync(_ => body(), cancellationToken);

    /// <summary>
    /// Принимает фразу: распознаёт интент, исполняет команду, при неясности отправляет на разбор.
    /// </summary>
    /// <param name="text">Фраза от человека.</param>
    /// <param name="source">Источник: telegram, voice, dashboard, mcp, api.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <param name="asrConfidence">Уверенность распознавания речи, если ввод был голосовым.</param>
    /// <param name="audioPath">Путь к записи, если её сохранили.</param>
    public async Task<IntakeResult> IntakeAsync(
        string text,
        string source,
        CancellationToken cancellationToken = default,
        double? asrConfidence = null,
        string? audioPath = null,
        string? intentHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string trimmed = text.Trim();
        long started = Stopwatch.GetTimestamp();
        IExecutionContext? context = _contexts.GetDefaultContext();

        // Классифицируем ровно один раз: запись в журнал делает сам классификатор.
        IntentResult intent = await _classifier.ClassifyAsync(trimmed, cancellationToken).ConfigureAwait(false);
        Guid? classificationId = intent.JournalId;

        // Вердикт правил запоминаем до подсказки: в журнале он должен остаться как был,
        // иначе по данным нельзя понять, насколько хорош классификатор.
        IntentResult classifierVerdict = intent;

        // Сессия сама сообщает интент (кнопка «Записать»/«Спросить» на станции): фраза — уже задача
        // или вопрос, вырезать из неё императив не нужно.
        if (!string.IsNullOrWhiteSpace(intentHint))
        {
            intent = intent with
            {
                Intent = intentHint.Trim(),
                Score = 1d,
                Model = intent.Model + "+session",
            };

            // Классификатор уже написал свою догадку (часто unclassified); подсказку храним
            // отдельной колонкой, не переписывая ни интент, ни score.
            if (classificationId is Guid hintedId)
            {
                await AnnotateJournalHintAsync(hintedId, intent.Intent, cancellationToken).ConfigureAwait(false);
            }
        }

        // Источник правды о том, откуда фраза, — приём: классификатор пишет значение по умолчанию.
        if (classificationId is Guid journalId)
        {
            await AnnotateJournalAsync(journalId, asrConfidence, audioPath, source, cancellationToken).ConfigureAwait(false);
        }

        // Отдельного порога для распознавания речи нет: sherpa-onnx не отдаёт вероятность,
        // а смысл фразы оценивает классификатор интента — он и решает, отправлять ли на разбор.
        if (!intent.IsConfident(_options.ConfidenceThreshold))
        {
            // Команды нет и не будет: непонятую фразу сохраняем как задачу на разбор —
            // это и есть датасет для будущей модели.
            var review = new IntentRoute(intent, null, null, false, null);

            // Задача, ссылка в журнале и запись действия — один набор: половину такого
            // набора видеть в базе бессмысленно, поэтому пишем одной транзакцией.
            (TaskRecord task, Guid reviewAction) = await InTransactionAsync(async () =>
            {
                TaskRecord created = await AddReviewTaskAsync(
                    trimmed, source, review, asrConfidence, audioPath, cancellationToken, classifierVerdict).ConfigureAwait(false);

                await LinkJournalAsync(classificationId, created.Id, cancellationToken).ConfigureAwait(false);

                Guid actionId = await RecordActionAsync(
                    created.Id, classificationId, source, intent.Intent, null, ActionOutcomes.NeedsReview,
                    null, null, null, started, cancellationToken).ConfigureAwait(false);

                return (created, actionId);
            }, cancellationToken).ConfigureAwait(false);

            return new IntakeResult(task, review, false, ActionOutcomes.NeedsReview, reviewAction);
        }

        // Вопрос — не задача, но и не мусор: ему нужна запись, чтобы ответ было где искать.
        // Задачу создаёт приём (а не команда), поэтому идентификатор сразу попадает в слоты.
        TaskRecord? recorded = null;
        if (IsRecorded(intent.Intent))
        {
            recorded = await InTransactionAsync(async () =>
            {
                TaskRecord question = await AddTaskAsync(
                    trimmed, source, new IntentRoute(intent, null, null, false, null), TaskStatuses.New,
                    ActionOutcomes.Executed, asrConfidence, audioPath, cancellationToken, classifierVerdict).ConfigureAwait(false);

                await LinkJournalAsync(classificationId, question.Id, cancellationToken).ConfigureAwait(false);
                return question;
            }, cancellationToken).ConfigureAwait(false);
        }

        // Уверенная фраза: всё уходит в роутер, он найдёт команду и вызовет диспатчер.
        IntentResult enriched = await EnrichSlotsAsync(intent, trimmed, source, recorded?.Id, cancellationToken).ConfigureAwait(false);
        IntentRoute route = await _router.RouteAsync(enriched, context, cancellationToken).ConfigureAwait(false);

        string? errorText = route.Error ?? PayloadError(route.Result);
        bool failed = errorText is not null;

        // Задачу могла создать команда (add_task): она явно помечает ответ флагом "created".
        Guid? taskId = ParseTaskId(route.Result);
        bool createdTask = !failed && IsCreated(route.Result);
        TaskRecord? created = createdTask && taskId is Guid id
            ? await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

        string outcome = failed
            ? ActionOutcomes.Failed
            : createdTask
                ? ActionOutcomes.Created
                : ActionOutcomes.Executed;

        // Мета задачи и запись действия — тоже один набор: иначе в базе остаётся действие
        // без задачи или задача, к которой нечем объяснить появление.
        Guid action = await InTransactionAsync(async () =>
        {
            // Команда создала задачу, но про распознавание ничего не знает: дописываем мету здесь,
            // иначе в tasks не останется следа, почему задача вообще появилась.
            if (created is not null)
            {
                string metaJson = BuildMeta(route, route.Executed, outcome, asrConfidence, audioPath, text: text, classifierVerdict: classifierVerdict);
                await _store.UpdateAsync(created.Id, null, null, null, metaJson, cancellationToken).ConfigureAwait(false);
                created.MetaJson = metaJson;
            }

            // Запись под вопрос: ответ команды кладём в мету задачи — это и есть «result таска».
            if (recorded is not null)
            {
                string metaJson = BuildMeta(route, route.Executed, outcome, asrConfidence, audioPath, ExtractAnswer(route.Result), text, classifierVerdict, ExtractString(route.Result, "answerer"));
                await _store.UpdateAsync(recorded.Id, null, null, null, metaJson, cancellationToken).ConfigureAwait(false);
                recorded.MetaJson = metaJson;
            }

            return await RecordActionAsync(
                taskId ?? recorded?.Id, classificationId, source, intent.Intent, route.CommandName, outcome,
                enriched.Slots?.ToJsonString(), route.Result?.ToJsonString(), errorText,
                started, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new IntakeResult(recorded ?? created, route, route.Executed && !failed, outcome, action);
    }

    /// <summary>
    /// Источник по умолчанию для запросов, где он не указан (у каждого хоста свой).
    /// </summary>
    public string DefaultSource => _options.DefaultSource;

    /// <summary>
    /// Порог уверенности, по которому приём решает «понятно / в разбор».
    /// </summary>
    /// <remarks>
    /// Хостам нужен тот же порог, что и сервису: иначе поле <c>confident</c> в ответе
    /// и решение о разборе считались бы по разным числам.
    /// </remarks>
    public double ConfidenceThreshold => _options.ConfidenceThreshold;

    /// <summary>
    /// Проверяет, что распознавание достаточной уверенности для автономного исполнения.
    /// </summary>
    /// <param name="route">Вердикт.</param>
    public bool IsConfident(IntentRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Intent.IsConfident(_options.ConfidenceThreshold);
    }

    private async Task<Guid> RecordActionAsync(
        Guid? taskId,
        Guid? classificationId,
        string source,
        string? intent,
        string? command,
        string outcome,
        string? argsJson,
        string? resultJson,
        string? error,
        long started,
        CancellationToken cancellationToken)
    {
        long durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var record = new ActionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            taskId,
            classificationId,
            source,
            ActionKinds.Intake,
            intent,
            command,
            outcome,
            argsJson,
            resultJson,
            error,
            durationMs);

        try
        {
            return await _actions.RecordAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!_options.ActionFailuresAreFatal)
        {
            return Guid.Empty;
        }
    }

    private async Task<TaskRecord> AddReviewTaskAsync(
        string text,
        string source,
        IntentRoute route,
        double? asrConfidence,
        string? audioPath,
        CancellationToken cancellationToken,
        IntentResult? classifierVerdict = null)
        => await AddTaskAsync(text, source, route, TaskStatuses.NeedsReview, ActionOutcomes.NeedsReview, asrConfidence, audioPath, cancellationToken, classifierVerdict).ConfigureAwait(false);

    /// <summary>
    /// Создаёт задачу-запись: либо «на разборе» (фразу не поняли), либо запись вопроса.
    /// </summary>
    private async Task<TaskRecord> AddTaskAsync(
        string text,
        string source,
        IntentRoute route,
        string status,
        string outcome,
        double? asrConfidence,
        string? audioPath,
        CancellationToken cancellationToken,
        IntentResult? classifierVerdict = null)
    {
        TaskRecord record = TaskFactory.Create(
            text,
            source,
            status,
            BuildMeta(route, commandExecuted: false, outcome, asrConfidence, audioPath, text: text, classifierVerdict: classifierVerdict));

        await _store.AddAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Связывает запись журнала с задачей: «эта фраза стала этой задачей».
    /// </summary>
    /// <remarks>
    /// Нужно для датасета: без ссылки нельзя выбрать все фразы, из которых родились задачи.
    /// Сбой журнала не ломает приём.
    /// </remarks>
    private async Task LinkJournalAsync(Guid? journalId, Guid taskId, CancellationToken cancellationToken)
    {
        if (journalId is not Guid id)
        {
            return;
        }

        try
        {
            await _journal.LinkToTaskAsync(id, taskId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!_options.ActionFailuresAreFatal)
        {
            // Журнал — вспомогательный: его сбой не должен ронять приём фразы.
        }
    }
    /// <summary>
    /// Дописывает к записи журнала уверенность ASR, путь к аудио и настоящий источник.
    /// </summary>
    /// <remarks>
    /// Классификатор пишет запись до того, как ему станет известно про звук,
    /// поэтому журнал правится отдельным шагом. Сбой журнала не ломает приём.
    /// </remarks>
    private async Task AnnotateJournalAsync(
        Guid journalId,
        double? asrConfidence,
        string? audioPath,
        string? source,
        CancellationToken cancellationToken)
    {

        try
        {
            await _journal.AnnotateAudioAsync(journalId, asrConfidence, audioPath, cancellationToken, source).ConfigureAwait(false);
        }
        catch (Exception) when (!_options.ActionFailuresAreFatal)
        {
            // Журнал — вспомогательный: его сбой не должен ронять приём фразы.
        }
    }

    /// <summary>
    /// Помечает запись журнала интентом сессии, не трогая вердикт классификатора.
    /// </summary>
    private async Task AnnotateJournalHintAsync(Guid journalId, string hintedIntent, CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AnnotateHintAsync(journalId, hintedIntent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!_options.ActionFailuresAreFatal)
        {
            // Журнал — вспомогательный: его сбой не должен ронять приём фразы.
        }
    }

    /// <summary>
    /// Проверяет, надо ли завести запись под фразу, которую не создаёт команда.
    /// </summary>
    private bool IsRecorded(string intent)
    {
        foreach (string candidate in _options.RecordedIntents)
        {
            if (string.Equals(candidate, intent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Подставляет слоты, которые знает только хост: текст фразы, источник и
    /// последнюю открытую задачу для <c>complete_task</c> без явного идентификатора.
    /// </summary>
    private async Task<IntentResult> EnrichSlotsAsync(
        IntentResult intent,
        string text,
        string source,
        Guid? recordId,
        CancellationToken cancellationToken)
    {
        // Запись под вопросом: команде нужен и текст, и идентификатор задачи, чтобы ответ
        // лёг в ту же строку.
        if (recordId is Guid recordedId)
        {
            return intent with
            {
                Slots = new JsonObject
                {
                    ["text"] = text,
                    ["taskId"] = recordedId.ToString(),
                },
            };
        }

        if (string.Equals(intent.Intent, "add_task", StringComparison.OrdinalIgnoreCase))
        {
            return intent with
            {
                Slots = new JsonObject
                {
                    ["text"] = text,
                    ["source"] = source,

                },
            };
        }

        if (!string.Equals(intent.Intent, "complete_task", StringComparison.OrdinalIgnoreCase))
        {
            return intent;
        }

        if (intent.Slots?["taskId"] is not null)
        {
            return intent;
        }

        TaskRecord? open = await FindLatestOpenAsync(cancellationToken).ConfigureAwait(false);
        if (open is null)
        {
            return intent;
        }

        var slots = intent.Slots is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
        slots["taskId"] = open.Id.ToString();
        return intent with { Slots = slots };
    }

    private async Task<TaskRecord?> FindLatestOpenAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskRecord> tasks = await _store.ListAsync(null, 50, cancellationToken).ConfigureAwait(false);

        foreach (TaskRecord task in tasks)
        {
            if (TaskStatuses.IsOpen(task.Status))
            {
                return task;
            }
        }

        return null;
    }

    private static bool IsCreated(JsonNode? result)
        => result is JsonObject payload
           && payload["created"] is JsonValue value
           && value.TryGetValue(out bool created)
           && created;

    private static Guid? ParseTaskId(JsonNode? result)
    {
        if (result is not JsonObject payload || payload["taskId"] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue(out string? text) && Guid.TryParse(text, out Guid id) ? id : null;
    }

    private static string? PayloadError(JsonNode? result)
        => result is JsonObject payload && payload["error"] is JsonNode error ? error.ToJsonString() : null;

    /// <summary>
    /// Достаёт ответ команды: он попадает в мету задачи-записи.
    /// </summary>
    private static string? ExtractAnswer(JsonNode? result)
        => result is JsonObject payload
           && payload["answer"] is JsonValue value
           && value.TryGetValue(out string? answer)
            ? answer
            : null;

    private static string? ExtractString(JsonNode? result, string name)
        => result is JsonObject payload
           && payload[name] is JsonValue value
           && value.TryGetValue(out string? text)
            ? text
            : null;

    private static string BuildMeta(
        IntentRoute route,
        bool commandExecuted,
        string outcome,
        double? asrConfidence = null,
        string? audioPath = null,
        string? answer = null,
        string? text = null,
        IntentResult? classifierVerdict = null,
        string? answerer = null)
    {
        var meta = new JsonObject
        {
            ["intent"] = route.Intent.Intent,
            ["score"] = route.Intent.Score,
            ["model"] = route.Intent.Model,
            ["command"] = route.CommandName,
            ["executed"] = commandExecuted,
            ["outcome"] = outcome,
            ["error"] = route.Error,
        };

        // Интент из сессии не должен прятать догадку классификатора: обе видны в мете,
        // и по задаче понятно, что решила кнопка, а что — правила.
        if (classifierVerdict is not null
            && !string.Equals(classifierVerdict.Intent, route.Intent.Intent, StringComparison.Ordinal))
        {
            meta["classifierIntent"] = classifierVerdict.Intent;
            meta["classifierScore"] = classifierVerdict.Score;
            meta["classifierModel"] = classifierVerdict.Model;
            meta["sessionIntent"] = route.Intent.Intent;
        }

        // Помечаем запись, если фраза пришла не по-русски: распознавание может
        // перевести её или записать латиницей, и такую запись потом видно в мете.
        if (TextLanguage.Detect(text) is string languageHint)
        {
            meta["textLanguage"] = languageHint;
            meta["textSample"] = TextLanguage.Describe(text, languageHint);
        }

        if (asrConfidence is not null)
        {
            meta["asrConfidence"] = asrConfidence.Value;
        }

        if (audioPath is not null)
        {
            meta["audioPath"] = audioPath;
        }

        if (answer is not null)
        {
            meta["answer"] = answer;
        }

        if (answerer is not null)
        {
            meta["answerer"] = answerer;
        }

        return meta.ToJsonString();
    }
}
