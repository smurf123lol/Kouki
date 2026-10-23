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
/// <param name="Outcome">Итог обработки фразы: см. <see cref="IntakeOutcomes"/>.</param>
/// <param name="ActionId">Запись журнала действий, по которой можно разобрать, что произошло.</param>
public sealed record IntakeResult(TaskRecord? Task, IntentRoute Route, bool AutoExecuted, string Outcome, Guid? ActionId = null);

/// <summary>
/// Итоги обработки фразы. Значения стабильны — на них опираются бот, дашборд и MCP.
/// </summary>
public static class IntakeOutcomes
{
    /// <summary>Команда создала задачу.</summary>
    public const string Created = "created";

    /// <summary>Задача на разборе: уверенности распознавания не хватило.</summary>
    public const string NeedsReview = "needs_review";

    /// <summary>Команда исполнена успешно; задача не создавалась.</summary>
    public const string Executed = "executed";

    /// <summary>Команда вернула ошибку; задача не создавалась.</summary>
    public const string Failed = "failed";
}

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
    public TaskIntakeService(
        IIntentClassifier classifier,
        IIntentRouter router,
        ITaskStore store,
        IActionLog actions,
        IContextRegistry contexts,
        IntakeOptions options,
        IClassificationJournal journal)
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
    }

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

        // Сессия сама сообщает интент (кнопка «Записать» на станции): фраза — уже задача,
        // вырезать из неё императив не нужно. Журнал при этом остаётся: классификатор уже написал запись.
        if (!string.IsNullOrWhiteSpace(intentHint))
        {
            intent = intent with
            {
                Intent = intentHint.Trim(),
                Score = 1d,
                Model = intent.Model + "+session",
            };
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
            TaskRecord task = await AddReviewTaskAsync(trimmed, source, review, asrConfidence, audioPath, cancellationToken).ConfigureAwait(false);

            Guid reviewAction = await RecordActionAsync(
                task.Id, classificationId, source, intent.Intent, null, IntakeOutcomes.NeedsReview,
                null, null, null, started, cancellationToken).ConfigureAwait(false);

            return new IntakeResult(task, review, false, IntakeOutcomes.NeedsReview, reviewAction);
        }

        // Уверенная фраза: всё уходит в роутер, он найдёт команду и вызовет диспатчер.
        IntentResult enriched = await EnrichSlotsAsync(intent, trimmed, source, cancellationToken).ConfigureAwait(false);
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
            ? IntakeOutcomes.Failed
            : createdTask
                ? IntakeOutcomes.Created
                : IntakeOutcomes.Executed;

        // Команда создала задачу, но про распознавание ничего не знает: дописываем мету здесь,
        // иначе в tasks не останется следа, почему задача вообще появилась.
        if (created is not null)
        {
            string metaJson = BuildMeta(route, route.Executed, outcome, asrConfidence, audioPath);
            await _store.UpdateAsync(created.Id, null, null, null, metaJson, cancellationToken).ConfigureAwait(false);
            created.MetaJson = metaJson;
        }

        Guid action = await RecordActionAsync(
            taskId, classificationId, source, intent.Intent, route.CommandName, outcome,
            enriched.Slots?.ToJsonString(), route.Result?.ToJsonString(), errorText,
            started, cancellationToken).ConfigureAwait(false);

        return new IntakeResult(created, route, route.Executed && !failed, outcome, action);
    }

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
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new TaskRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O"),
            Source = source,
            RawText = text,
            Title = TaskTitles.Make(text),
            Status = TaskStatuses.NeedsReview,
            Priority = 2,
            MetaJson = BuildMeta(route, commandExecuted: false, IntakeOutcomes.NeedsReview, asrConfidence, audioPath),
        };

        await _store.AddAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Дописывает к записи журнала уверенность ASR и путь к аудио.
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
    /// Подставляет слоты, которые знает только хост: текст фразы, источник и
    /// последнюю открытую задачу для <c>complete_task</c> без явного идентификатора.
    /// </summary>
    private async Task<IntentResult> EnrichSlotsAsync(
        IntentResult intent,
        string text,
        string source,
        CancellationToken cancellationToken)
    {
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
            if (task.Status is TaskStatuses.New or TaskStatuses.InProgress)
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

    private static string BuildMeta(
        IntentRoute route,
        bool commandExecuted,
        string outcome,
        double? asrConfidence = null,
        string? audioPath = null)
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

        if (asrConfidence is not null)
        {
            meta["asrConfidence"] = asrConfidence.Value;
        }

        if (audioPath is not null)
        {
            meta["audioPath"] = audioPath;
        }

        return meta.ToJsonString();
    }
}
