using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Commands;
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
public sealed record IntakeResult(TaskRecord? Task, IntentRoute Route, bool AutoExecuted, string Outcome, Guid? ActionId = null)
{
    /// <summary>
    /// Теги, которые действительно записаны задаче (и из фразы, и от модели).
    /// </summary>
    /// <remarks>
    /// Хосту они нужны сразу в ответе: иначе после приёма пришлось бы отдельно спрашивать
    /// задачу, чтобы показать человеку, чем она помечена.
    /// </remarks>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
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
    private readonly IUnitOfWork? _work;
    private readonly ITagStore? _tags;
    private readonly IQuestionStore _questions;

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
    /// <param name="tags">Хранилище тегов.</param>
    /// <param name="questions">Хранилище вопросов (T105).</param>
    public TaskIntakeService(
        IIntentClassifier classifier,
        IIntentRouter router,
        ITaskStore store,
        IActionLog actions,
        IContextRegistry contexts,
        IntakeOptions options,
        IClassificationJournal journal,
        IUnitOfWork? work = null,
        ITagStore? tags = null,
        IQuestionStore? questions = null)
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
        _tags = tags;
        _questions = questions ?? NullQuestionStore.Instance;
    }

    /// <summary>
    /// Прикрепляет теги задаче. Без хранилища тегов (юнит-тесты, чужие хосты)
    /// вызов ничего не делает: теги — необязательная часть приёма.
    /// </summary>
    /// <param name="taskId">Задача.</param>
    /// <param name="tags">Теги в канонической форме.</param>
    /// <param name="source">Источник тега: <see cref="TagSources.Text"/> или <see cref="TagSources.Model"/>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task AttachTagsAsync(
        Guid taskId,
        IReadOnlyList<string> tags,
        string source,
        CancellationToken cancellationToken)
    {
        if (_tags is null || tags.Count == 0)
        {
            return;
        }

        await _tags.AddAsync(taskId, tags, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Приводит теги модели к канонической форме и общим лимитам.
    /// </summary>
    /// <remarks>
    /// Модель — источник предложений, а не истины: она может вернуть «#Работа», повторы
    /// и больше <see cref="TagParser.MaxTags"/> записей. Решение, что писать, принимает приём,
    /// а не промпт: классификатор живёт в Kouki.Yasabi и не знает ни про TagParser, ни про лимиты.
    /// </remarks>
    /// <param name="tags">Теги из вердикта модели.</param>
    /// <param name="textTags">Теги, уже вырезанные из фразы.</param>
    /// <returns>Теги для записи со source=<see cref="TagSources.Model"/>, без повторов с текстовыми.</returns>
    internal static IReadOnlyList<string> NormalizeModelTags(
        IReadOnlyList<string>? tags,
        IReadOnlyList<string> textTags)
    {
        if (tags is null || tags.Count == 0)
        {
            return Array.Empty<string>();
        }

        int room = TagParser.MaxTags - textTags.Count;
        if (room <= 0)
        {
            return Array.Empty<string>();
        }

        List<string> modelTags = new();
        foreach (string raw in tags)
        {
            string tag = TagParser.Normalize(raw);
            if (tag.Length == 0
                || textTags.Contains(tag, StringComparer.OrdinalIgnoreCase)
                || modelTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            modelTags.Add(tag);
            if (modelTags.Count == room)
            {
                break;
            }
        }

        return modelTags;
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
    /// <param name="intentHint">Интент сессии, если хост его знает.</param>
    /// <param name="tags">
    /// Теги, уже вырезанные границей приёма (<see cref="IntakeRequestPolicy"/>) по режиму источника;
    /// <c>null</c> — прямой вызов без границы, тогда режим и теги считаются здесь.
    /// </param>
    /// <exception cref="IntakeRejectionException">Фраза состояла только из тегов.</exception>
    public async Task<IntakeResult> IntakeAsync(
        string text,
        string source,
        CancellationToken cancellationToken = default,
        double? asrConfidence = null,
        string? audioPath = null,
        string? intentHint = null,
        IReadOnlyList<string>? tags = null,
        bool continueDialogue = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string trimmed = text.Trim();

        // Теги в фразе (`#работа`, `[срочно]`) — часть метки, а не формулировки: в режиме
        // extract они вырезаются из текста и уходят в task_tags отдельными строками.
        // Режим — на источник: станция диктует метку голосом, API присылает тег решёткой.
        string tagMode = _options.ResolveTagMode(source);
        IReadOnlyList<string> textTags;
        if (tags is null)
        {
            (trimmed, textTags) = TagParser.Split(trimmed, tagMode);
        }
        else
        {
            textTags = tags;

            // Граница уже вырезала теги и проверила фразу — второй раз режем только текст.
            if (!string.Equals(tagMode, TagModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = TagParser.Clean(trimmed);
            }
        }

        if (trimmed.Length == 0)
        {
            // Защита для прямых вызовов без границы: отдаём тот же код, что и HTTP/MCP.
            throw new IntakeRejectionException(IntakeRequestPolicy.TagsOnlyText, IntakeRequestPolicy.TagsOnlyMessage);
        }

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

        // Теги модели — предложения: нормализуем, убираем повторы и соблюдаем общий лимит.
        // Текстовые теги идут первыми: их человек поставил сам, и при совпадении остаётся source=text.
        IReadOnlyList<string> modelTags = NormalizeModelTags(intent.Tags, textTags);
        IReadOnlyList<string> persistedTags = textTags.Concat(modelTags).ToArray();

        // Проверяем диалоговый follow-up: продолжение диалога с фразой «запиши это / сохрани в задачи».
        string? dialogueQuestion = null;
        string? dialogueAnswer = null;
        if (continueDialogue && !string.IsNullOrWhiteSpace(source) && context?.StateStore is not null)
        {
            string stateKey = $"dialogue:last:{source.Trim()}";
            var lastTurn = await context.StateStore.GetAsync<LastDialogueTurn>(stateKey, cancellationToken).ConfigureAwait(false);
            if (lastTurn is not null && DateTimeOffset.UtcNow - lastTurn.At <= AskHandler.DialogueTtl)
            {
                dialogueQuestion = lastTurn.Question;
                dialogueAnswer = lastTurn.Answer;
            }
        }

        bool isDialogueSave = dialogueQuestion is not null && IsSaveDialoguePhrase(trimmed);
        if (isDialogueSave)
        {
            intent = intent with
            {
                Intent = "add_task",
                Score = 1.0,
                Model = intent.Model + "+dialogue_save",
            };
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

                await AttachTagsAsync(created.Id, textTags, TagSources.Text, cancellationToken).ConfigureAwait(false);
                await AttachTagsAsync(created.Id, modelTags, TagSources.Model, cancellationToken).ConfigureAwait(false);

                Guid actionId = await RecordActionAsync(
                    created.Id, classificationId, source, intent.Intent, null, ActionOutcomes.NeedsReview,
                    null, null, null, started, cancellationToken).ConfigureAwait(false);

                return (created, actionId);
            }, cancellationToken).ConfigureAwait(false);

            return new IntakeResult(task, review, false, ActionOutcomes.NeedsReview, reviewAction)
            {
                Tags = persistedTags,
            };
        }

        // Уверенная фраза: всё уходит в роутер, он найдёт команду и вызовет диспатчер.
        string effectiveText = isDialogueSave && !string.IsNullOrWhiteSpace(dialogueQuestion) ? dialogueQuestion : trimmed;
        IntentResult enriched = await EnrichSlotsAsync(intent, effectiveText, source, null, continueDialogue, cancellationToken).ConfigureAwait(false);
        IntentRoute route = await _router.RouteAsync(enriched, context, cancellationToken).ConfigureAwait(false);

        string? errorText = route.Error ?? PayloadError(route.Result);
        bool failed = errorText is not null;

        // Вопрос пишется в журнал вопросов, не создавая задач на доске.
        if (string.Equals(intent.Intent, "ask", StringComparison.OrdinalIgnoreCase))
        {
            string answer = ExtractAnswer(route.Result) ?? string.Empty;
            string? model = ExtractString(route.Result, "answerer");
            long? duration = route.Result?["answerMs"]?.GetValue<long?>();

            var questionRecord = new QuestionRecord(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                source,
                trimmed,
                answer,
                TaskId: null,
                Model: model,
                DurationMs: duration,
                AudioPath: audioPath);

            await _questions.AddAsync(questionRecord, cancellationToken).ConfigureAwait(false);
        }

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
                string metaJson = BuildMeta(route, route.Executed, outcome, asrConfidence, audioPath, isDialogueSave ? dialogueAnswer : null, text: effectiveText, classifierVerdict: classifierVerdict);
                await _store.UpdateAsync(created.Id, null, null, null, metaJson, cancellationToken).ConfigureAwait(false);

                // Команда создала задачу — но не знает про журнал распознавания: ссылка
                // «эта фраза стала этой задачей» нужна датасету, как и у задач на разборе.
                await LinkJournalAsync(classificationId, created.Id, cancellationToken).ConfigureAwait(false);
                await AttachTagsAsync(created.Id, textTags, TagSources.Text, cancellationToken).ConfigureAwait(false);
                created.MetaJson = metaJson;

                // Фоновые автотеги модели (П4): не блокируют транзакцию и отдачу ответа пользователю/станции
                if (modelTags.Count > 0)
                {
                    if (_options.AsyncModelTagging)
                    {
                        Guid taskIdForTags = created.Id;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await AttachTagsAsync(taskIdForTags, modelTags, TagSources.Model, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Фоновое добавление тегов модели не должно ронять процесс
                            }
                        });
                    }
                    else
                    {
                        await AttachTagsAsync(created.Id, modelTags, TagSources.Model, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (isDialogueSave && dialogueQuestion is not null)
                {
                    IReadOnlyList<QuestionRecord> recent = await _questions.ListAsync(5, 0, dialogueQuestion, cancellationToken).ConfigureAwait(false);
                    if (recent.Count > 0)
                    {
                        await _questions.LinkTaskAsync(recent[0].Id, created.Id, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return await RecordActionAsync(
                taskId, classificationId, source, intent.Intent, route.CommandName, outcome,
                enriched.Slots?.ToJsonString(), route.Result?.ToJsonString(), errorText,
                started, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new IntakeResult(created, route, route.Executed && !failed, outcome, action)
        {
            Tags = persistedTags,
        };
    }

    /// <summary>
    /// Источник по умолчанию для запросов, где он не указан (у каждого хоста свой).
    /// </summary>
    public string DefaultSource => _options.DefaultSource;

    /// <summary>
    /// Режим тегов для источника: хосту он нужен до <see cref="IntakeRequestPolicy.TryPrepare"/>,
    /// чтобы граница знала, вырезать ли решётки из фразы.
    /// </summary>
    /// <param name="source">Источник фразы, см. <see cref="Contracts.TaskSources"/>.</param>
    /// <returns>Режим из <see cref="TagModes"/>.</returns>
    public string ResolveTagMode(string? source) => _options.ResolveTagMode(source);

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

    private static bool IsSaveDialoguePhrase(string text)
    {
        string lower = text.Trim().ToLowerInvariant();
        return lower is "запиши это" or "сохрани это" or "запиши в задачи" or "сохрани в задачи"
            or "сделай задачей" or "в задачи" or "запиши" or "сохрани"
            || ((lower.StartsWith("запиши") || lower.StartsWith("сохрани")) && lower.Length <= 30);
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
        bool continueDialogue,
        CancellationToken cancellationToken)
    {
        if (string.Equals(intent.Intent, "ask", StringComparison.OrdinalIgnoreCase))
        {
            return intent with
            {
                Slots = new JsonObject
                {
                    ["text"] = text,
                    ["source"] = source,
                    ["continueDialogue"] = continueDialogue,
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

        if (string.Equals(intent.Intent, "add_reminder", StringComparison.OrdinalIgnoreCase))
        {
            // Фразу разбирает ReminderTime: «напомни через 20 минут полить цветы» —
            // время отделяется от текста уже в команде, приём лишь передаёт фразу.
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
            meta["hasAudio"] = true;
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
