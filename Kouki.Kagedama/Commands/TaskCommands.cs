using System.Text.Json.Nodes;
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Commands;

/// <summary>
/// Запрос на создание задачи.
/// </summary>
/// <param name="Text">Фраза, из которой берётся заголовок.</param>
/// <param name="Source">Откуда пришла фраза, см. <see cref="TaskSources"/>.</param>
/// <param name="ParentId">Родительская задача, если создаётся подзадача.</param>
/// <param name="Priority">Приоритет с доски; <c>null</c> — обычный.</param>
public sealed record AddTaskCommand(
    string? Text = null,
    string? Source = null,
    Guid? ParentId = null,
    int? Priority = null,
    string? Title = null) : ICommand;

/// <summary>
/// Создаёт задачу: единственная команда, которая это делает.
/// Приём фразы лишь распознаёт интент и подставляет слоты, запись в базу делает эта команда,
/// кроме одного случая — неясная фраза, для которой команды не существует (её сохраняет приём).
/// </summary>
[Command(
    "add_task",
    Description = "Создаёт задачу из фразы",
    Category = CommandCategory.Navigation,
    Example = "запиши задачу купить хлеб")]
public sealed class AddTaskHandler : ICommandHandler<AddTaskCommand>
{
    private readonly ITaskStore _store;
    private readonly ITaskTitleGenerator? _titleGenerator;

    /// <summary>
    /// Создаёт обработчик создания задачи.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    /// <param name="titleGenerator">Генератор чистого заголовка; при null заголовок строится по TaskTitles.Make.</param>
    public AddTaskHandler(ITaskStore store, ITaskTitleGenerator? titleGenerator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _titleGenerator = titleGenerator;
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        AddTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return new JsonObject { ["error"] = "Пустая фраза: нечего записывать как задачу.", ["answer"] = "Пустая фраза: нечего записывать как задачу." };
        }

        if (command.Priority is { } priority && !TaskPriorities.IsValid(priority))
        {
            return new JsonObject
            {
                ["error"] = $"Приоритет: {TaskPriorities.Min}, {TaskPriorities.Default} или {TaskPriorities.Max}.",
                ["answer"] = $"Приоритет: {TaskPriorities.Min}, {TaskPriorities.Default} или {TaskPriorities.Max}.",
            };
        }

        // Родителя проверяем до записи: у подзадачи дерево и позиция считаются по нему,
        // а «родитель не найден» должен быть человеческим отказом, а не исключением хранилища.
        if (command.ParentId is { } parentId)
        {
            TaskRecord? parent = await _store.GetAsync(parentId, cancellationToken).ConfigureAwait(false);
            if (parent is null)
            {
                return new JsonObject { ["error"] = "Родительская задача не найдена.", ["answer"] = "Родительская задача не найдена." };
            }
        }

        string source = string.IsNullOrWhiteSpace(command.Source) ? TaskSources.Command : command.Source.Trim();
        string rawText = command.Text.Trim();
        string cleanTitle = !string.IsNullOrWhiteSpace(command.Title)
            ? command.Title.Trim()
            : _titleGenerator is not null
                ? await _titleGenerator.GenerateTitleAsync(rawText, cancellationToken).ConfigureAwait(false)
                : TaskTitles.Make(rawText);

        TaskRecord record = TaskFactory.Create(rawText, source);
        record.Title = cleanTitle;
        record.RawText = rawText;

        // Дерево и позицию досчитывает хранилище в той же транзакции, что и вставку.
        record.ParentId = command.ParentId;
        if (command.Priority is { } chosen)
        {
            record.Priority = chosen;
        }

        try
        {
            await _store.AddAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Родителя могли удалить между проверкой и вставкой: ответ тот же, что и выше.
            return new JsonObject { ["error"] = "Родительская задача не найдена.", ["answer"] = "Родительская задача не найдена." };
        }

        JsonObject payload = ListTasksHandler.ToJson(record);

        // Приём фразы узнаёт по этим двум полям, что задача действительно создана.
        payload["taskId"] = record.Id.ToString();
        payload["created"] = true;
        payload["answer"] = $"Записала: {record.Title}";
        return payload;
    }
}

/// <summary>
/// Запрос списка задач.
/// </summary>
/// <param name="Status">Фильтр по статусу или null.</param>
/// <param name="Limit">Сколько показать; по умолчанию пятьдесят.</param>
/// <param name="Tag">Фильтр по тегу: показывать только задачи с этой меткой.</param>
/// <param name="ParentId">Только прямые дети этой задачи.</param>
/// <param name="TopLevel">true — только корни без родителя.</param>
public sealed record ListTasksCommand(
    string? Status = null,
    int? Limit = null,
    string? Tag = null,
    Guid? ParentId = null,
    bool TopLevel = false) : ICommand;


/// <summary>
/// Показывает последние задачи, ничего не меняя.
/// Возвращает и служебные поля, чтобы HTTP-слой не ходил в хранилище напрямую.
/// </summary>
[Command(
    "list_tasks",
    Description = "Показывает последние задачи",
    Category = CommandCategory.Navigation,
    Example = "покажи мои задачи")]
public sealed class ListTasksHandler : ICommandHandler<ListTasksCommand>
{
    /// <summary>
    /// Текст отказа, когда после нормализации тег пуст: «#» — не тег.
    /// </summary>
    public const string EmptyTagError = "Тег не может быть пустым.";

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<string>> NoTags =
        new Dictionary<Guid, IReadOnlyList<string>>();

    private readonly ITaskStore _store;
    private readonly ITagStore? _tags;

    /// <summary>
    /// Создаёт обработчик списка задач.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    /// <param name="tags">Хранилище тегов: без него задачи отдаются с пустым <c>tags</c>.</param>
    public ListTasksHandler(ITaskStore store, ITagStore? tags = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tags = tags;
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        ListTasksCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int limit = Math.Clamp(command.Limit ?? 50, 1, 500);
        string? status = TaskStatuses.IsKnown(command.Status) ? command.Status : null;

        IReadOnlyList<TaskRecord> tasks;
        string? tag = null;

        if (!string.IsNullOrWhiteSpace(command.Tag))
        {
            // Тег приходит как его написал человек («#Работа»), в базе он канонический.
            tag = TagParser.Normalize(command.Tag);
            if (tag.Length == 0)
            {
                return new JsonObject
                {
                    ["count"] = 0,
                    ["status"] = status,
                    ["tasks"] = new JsonArray(),
                    ["error"] = EmptyTagError,
                };
            }

            tasks = _tags is null
                ? Array.Empty<TaskRecord>()
                : await FindByTagAsync(tag, status, limit, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            tasks = await _store
                .ListAsync(status, limit, cancellationToken, command.TopLevel, command.ParentId)
                .ConfigureAwait(false);
        }

        // Тег и дерево можно задать вместе: поиск по тегу про дерево не знает, поэтому
        // фильтр по родителю применяется к уже найденному списку.
        if (command.ParentId is { } parentId)
        {
            tasks = tasks.Where(task => task.ParentId == parentId).ToList();
        }
        else if (command.TopLevel)
        {
            tasks = tasks.Where(task => task.ParentId is null).ToList();
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<string>> tags = await LoadTagsAsync(tasks, cancellationToken).ConfigureAwait(false);

        // Счётчики подзадач — один запрос на весь список: карточкам доски нужны обе цифры.
        IReadOnlyDictionary<Guid, SubtaskCounts> counts = await _store
            .CountSubtasksAsync(tasks.Select(task => task.Id).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var array = new JsonArray();
        foreach (TaskRecord task in tasks)
        {
            array.Add(ToJson(
                task,
                tags.TryGetValue(task.Id, out IReadOnlyList<string>? taskTags) ? taskTags : null,
                counts.TryGetValue(task.Id, out SubtaskCounts taskCounts) ? taskCounts : default));
        }

        string answerText = array.Count switch
        {
            0 => "У вас нет открытых задач.",
            1 => $"У вас одна задача: {tasks[0].Title}.",
            _ => $"У вас {FormatTaskCount(array.Count)}."
        };

        return new JsonObject
        {
            ["count"] = array.Count,
            ["status"] = status,
            ["tag"] = tag,
            ["tasks"] = array,
            ["answer"] = answerText,
        };
    }

    /// <summary>
    /// Задачи с тегом: идентификаторы даёт <see cref="ITagStore.FindTasksAsync"/>,
    /// который фильтрует тег и статус одним запросом — предел считается уже по ним.
    /// </summary>
    private static string FormatTaskCount(int count)
    {
        int mod100 = count % 100;
        int mod10 = count % 10;
        if (mod100 is >= 11 and <= 19) return $"{count} задач";
        return mod10 switch
        {
            1 => $"{count} задача",
            2 or 3 or 4 => $"{count} задачи",
            _ => $"{count} задач"
        };
    }

    private async Task<IReadOnlyList<TaskRecord>> FindByTagAsync(
        string tag,
        string? status,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> ids = await _tags!
            .FindTasksAsync(tag, status, limit, cancellationToken)
            .ConfigureAwait(false);

        List<TaskRecord> found = new(ids.Count);
        foreach (Guid id in ids)
        {
            TaskRecord? task = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                continue;
            }

            found.Add(task);
        }

        return found;
    }

    /// <summary>
    /// Теги всех задач списка одним запросом: список обновляется дашбордом каждые пять секунд.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> LoadTagsAsync(
        IReadOnlyList<TaskRecord> tasks,
        CancellationToken cancellationToken)
    {
        if (_tags is null || tasks.Count == 0)
        {
            return NoTags;
        }

        return await _tags
            .ListForTasksAsync(tasks.Select(task => task.Id).ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Представление задачи для хостов. <c>tags</c> есть всегда — пустой массив, а не <c>null</c>.
    /// </summary>
    /// <param name="task">Задача.</param>
    /// <param name="tags">Теги задачи; <c>null</c> — пусто.</param>
    /// <param name="counts">Счётчики прямых подзадач; по умолчанию нули.</param>
    internal static JsonObject ToJson(
        TaskRecord task,
        IReadOnlyList<string>? tags = null,
        SubtaskCounts counts = default)
    {
        // Теги читаются из базы как есть: битые и пустые строки не должны попадать в JSON.
        var array = new JsonArray();
        foreach (string tag in tags ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                array.Add(tag);
            }
        }

        return new JsonObject
        {
            ["id"] = task.Id.ToString(),
            ["title"] = task.Title,
            ["status"] = task.Status,
            ["priority"] = task.Priority,
            ["createdAt"] = task.CreatedAt,
            ["updatedAt"] = task.UpdatedAt,
            ["rawText"] = task.RawText,
            ["source"] = task.Source,
            ["workspace"] = task.Workspace,
            ["parentId"] = task.ParentId?.ToString(),
            ["treeId"] = task.TreeId.ToString(),
            ["position"] = task.Position,
            ["subtasksCount"] = counts.Total,
            ["subtasksDoneCount"] = counts.Done,
            ["tags"] = array,
            ["meta"] = ParseMeta(task.MetaJson),
        };
    }

    private static JsonNode? ParseMeta(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(metaJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(metaJson);
        }
    }
}

/// <summary>
/// Запрос на чтение одной задачи.
/// </summary>
/// <param name="TaskId">Идентификатор задачи (GUID строкой).</param>
public sealed record GetTaskCommand(string? TaskId = null) : ICommand;

/// <summary>
/// Читает одну задачу по идентификатору.
/// </summary>
/// <remarks>
/// Существует ради полноты: HTTP-хост не должен ходить в хранилище мимо команд. Ответ —
/// тот же объект задачи, что отдаёт <c>list_tasks</c>, плюс поля <c>found</c>/<c>error</c>.
/// </remarks>
[Command(
    "get_task",
    Description = "Читает одну задачу по идентификатору",
    Category = CommandCategory.Navigation,
    RequiresConfirmation = false)]
public sealed class GetTaskHandler : ICommandHandler<GetTaskCommand>
{
    private readonly ITaskStore _store;
    private readonly ITagStore? _tags;

    /// <summary>
    /// Создаёт обработчик чтения задачи.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    /// <param name="tags">Хранилище тегов: без него задача отдаётся без тегов.</param>
    public GetTaskHandler(ITaskStore store, ITagStore? tags = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tags = tags;
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        GetTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Guid.TryParse(command.TaskId, out Guid id))
        {
            return new JsonObject
            {
                ["found"] = false,
                ["error"] = "Не указан идентификатор задачи.",
            };
        }

        TaskRecord? task = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (task is null)
        {
            return new JsonObject
            {
                ["found"] = false,
                ["error"] = $"Задача {id} не найдена.",
            };
        }

        IReadOnlyList<string> taskTags = _tags is null
            ? Array.Empty<string>()
            : await _tags.ListAsync(id, cancellationToken).ConfigureAwait(false);

        // Счётчики подзадач — отдельным запросом: get_task читает одну задачу, но наружу
        // отдаёт те же поля, что list_tasks, иначе карточка покажет «0 подзадач».
        IReadOnlyDictionary<Guid, SubtaskCounts> counts = await _store
            .CountSubtasksAsync(new[] { id }, cancellationToken).ConfigureAwait(false);
        SubtaskCounts subtaskCounts = counts.TryGetValue(id, out SubtaskCounts value) ? value : default;

        JsonObject payload = ListTasksHandler.ToJson(task, taskTags, subtaskCounts);
        payload["found"] = true;
        return payload;
    }
}

/// <summary>
/// Запрос на закрытие задачи.
/// </summary>
/// <param name="TaskId">Идентификатор задачи; если не задан, закрывается последняя открытая.</param>
public sealed record CompleteTaskCommand(string? TaskId = null) : ICommand;

/// <summary>
/// Закрывает задачу. Правило «без идентификатора закрываем последнюю открытую»
/// живёт здесь, а не в хостах: иначе каждый хост реализует его по-своему.
/// </summary>
[Command(
    "complete_task",
    Description = "Закрывает задачу по идентификатору",
    Category = CommandCategory.Navigation,
    Example = "готово, закрывай задачу")]
public sealed class CompleteTaskHandler : ICommandHandler<CompleteTaskCommand>
{
    private readonly ITaskStore _store;
    private readonly IActionLog? _actions;

    /// <summary>
    /// Создаёт обработчик закрытия задачи.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    /// <param name="actions">Журнал действий; без него команда работает, но без следа.</param>
    public CompleteTaskHandler(ITaskStore store, IActionLog? actions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _actions = actions;
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        CompleteTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Guid? requested = Guid.TryParse(command.TaskId, out Guid parsed) ? parsed : null;
        TaskRecord? existing = requested is Guid id
            ? await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)
            : await FindLatestOpenAsync(cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            string message = requested is Guid missing
                ? $"Задача {missing} не найдена."
                : "Открытых задач нет: нечего закрывать.";
            return new JsonObject { ["error"] = message, ["answer"] = message };
        }

        if (existing.Status is TaskStatuses.Done or TaskStatuses.Cancelled)
        {
            return new JsonObject
            {
                ["taskId"] = existing.Id.ToString(),
                ["status"] = existing.Status,
                ["alreadyClosed"] = true,
                ["answer"] = "Эта задача уже была закрыта.",
            };
        }

        await _store.UpdateAsync(existing.Id, TaskStatuses.Done, null, null, cancellationToken: cancellationToken).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["taskId"] = existing.Id.ToString(),
            ["title"] = existing.Title,
            ["status"] = TaskStatuses.Done,
            ["answer"] = $"Задача завершена: {existing.Title}",
        };

        await CommandAudit.RecordAsync(
            _actions,
            TaskSources.Command,
            "complete_task",
            existing.Id,
            new JsonObject { ["taskId"] = existing.Id.ToString() },
            payload,
            null,
            cancellationToken).ConfigureAwait(false);

        return payload;
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
}

/// <summary>
/// Запрос на изменение задачи: статус, заголовок, приоритет.
/// </summary>
/// <param name="TaskId">Идентификатор задачи.</param>
/// <param name="Status">Новый статус или null.</param>
/// <param name="Title">Новый заголовок или null.</param>
/// <param name="Priority">Новый приоритет или null.</param>
public sealed record PatchTaskCommand(string? TaskId = null, string? Status = null, string? Title = null, int? Priority = null) : ICommand;

/// <summary>
/// Меняет задачу — единственный путь записи из HTTP-слоя.
/// Раньше <c>PATCH /api/tasks</c> писал в хранилище напрямую и не оставлял записи в журнале действий.
/// </summary>
[Command(
    "patch_task",
    Description = "Меняет статус, заголовок или приоритет задачи",
    Category = CommandCategory.Navigation,
    Example = "переведи задачу в работу")]
public sealed class PatchTaskHandler : ICommandHandler<PatchTaskCommand>
{
    private readonly ITaskStore _store;
    private readonly IActionLog? _actions;

    /// <summary>
    /// Создаёт обработчик изменения задачи.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    /// <param name="actions">Журнал действий; без него команда работает, но без следа.</param>
    public PatchTaskHandler(ITaskStore store, IActionLog? actions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _actions = actions;
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        PatchTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Guid.TryParse(command.TaskId, out Guid id))
        {
            return new JsonObject { ["error"] = "Не указан идентификатор задачи.", ["answer"] = "Не указан идентификатор задачи." };
        }

        if (command.Status is not null && !TaskStatuses.IsKnown(command.Status))
        {
            return new JsonObject { ["error"] = $"Неизвестный статус '{command.Status}'.", ["answer"] = $"Неизвестный статус {command.Status}." };
        }

        if (command.Priority is int priority && !TaskPriorities.IsValid(priority))
        {
            return new JsonObject { ["error"] = $"Приоритет: {TaskPriorities.Min}, {TaskPriorities.Default} или {TaskPriorities.Max}.", ["answer"] = $"Приоритет: {TaskPriorities.Min}, {TaskPriorities.Default} или {TaskPriorities.Max}." };
        }

        bool updated = await _store
            .UpdateAsync(id, command.Status, command.Title, command.Priority, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!updated)
        {
            return new JsonObject { ["error"] = "Задача не найдена.", ["answer"] = "Задача не найдена." };
        }

        TaskRecord? task = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return new JsonObject { ["error"] = "Задача не найдена.", ["answer"] = "Задача не найдена." };
        }

        // Как и get_task: счётчики читаются здесь, чтобы PATCH не отдавал нули.
        IReadOnlyDictionary<Guid, SubtaskCounts> counts = await _store
            .CountSubtasksAsync(new[] { task.Id }, cancellationToken).ConfigureAwait(false);
        SubtaskCounts subtaskCounts = counts.TryGetValue(task.Id, out SubtaskCounts value) ? value : default;

        JsonObject payload = ListTasksHandler.ToJson(task, counts: subtaskCounts);

        string answerText = command.Status is not null
            ? $"Статус задачи изменен на {command.Status}: {task.Title}"
            : (command.Title is not null ? $"Задача переименована: {task.Title}" : $"Задача обновлена: {task.Title}");
        payload["answer"] = answerText;

        await CommandAudit.RecordAsync(
            _actions,
            TaskSources.Command,
            "patch_task",
            task.Id,
            new JsonObject
            {
                ["taskId"] = task.Id.ToString(),
                ["status"] = command.Status,
                ["title"] = command.Title,
                ["priority"] = command.Priority,
            },
            payload,
            null,
            cancellationToken).ConfigureAwait(false);

        return payload;
    }
}

/// <summary>
/// Запрос на перенос задачи в колонку и на позицию.
/// </summary>
/// <param name="TaskId">Идентификатор задачи.</param>
/// <param name="Status">Целевой статус.</param>
/// <param name="Position">Позиция в колонке, начиная с нуля.</param>
public sealed record MoveTaskCommand(string? TaskId = null, string? Status = null, int? Position = null) : ICommand;

/// <summary>
/// Переносит карточку: смена статуса и позиции одной операцией.
/// </summary>
/// <remarks>
/// Отдельная команда, а не <c>patch_task</c>: перестановка двигает соседей, и делать это
/// частичным обновлением нельзя — на доске появились бы две карточки с одной позицией.
/// </remarks>
[Command(
    "move_task",
    Description = "Переносит задачу в колонку и на позицию",
    Category = CommandCategory.Navigation,
    Example = "передвинь задачу в работу")]
public sealed class MoveTaskHandler : ICommandHandler<MoveTaskCommand>
{
    /// <summary>Верхняя граница позиции: защита от мусора в запросе.</summary>
    public const int MaxPosition = 10_000;

    private readonly ITaskStore _store;
    private readonly IActionLog? _actions;

    /// <summary>
    /// Создаёт обработчик переноса задачи.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    /// <param name="actions">Журнал действий; без него команда работает, но без следа.</param>
    public MoveTaskHandler(ITaskStore store, IActionLog? actions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _actions = actions;
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        MoveTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Guid.TryParse(command.TaskId, out Guid id))
        {
            return new JsonObject { ["error"] = "Не указан идентификатор задачи." };
        }

        if (string.IsNullOrWhiteSpace(command.Status))
        {
            return new JsonObject { ["error"] = "Укажите status." };
        }

        if (!TaskStatuses.IsKnown(command.Status))
        {
            return new JsonObject { ["error"] = $"Неизвестный статус '{command.Status}'." };
        }

        if (command.Position is not int position || position < TaskPositions.First || position > MaxPosition)
        {
            return new JsonObject { ["error"] = "position: целое число от 0." };
        }

        TaskRecord? moved = await _store
            .MoveAsync(id, command.Status, position, cancellationToken)
            .ConfigureAwait(false);

        if (moved is null)
        {
            return new JsonObject { ["error"] = $"Задача {id} не найдена." };
        }

        IReadOnlyDictionary<Guid, SubtaskCounts> counts = await _store
            .CountSubtasksAsync(new[] { moved.Id }, cancellationToken)
            .ConfigureAwait(false);

        JsonObject payload = ListTasksHandler.ToJson(
            moved,
            null,
            counts.TryGetValue(moved.Id, out SubtaskCounts taskCounts) ? taskCounts : default);

        await CommandAudit.RecordAsync(
            _actions,
            TaskSources.Command,
            "move_task",
            moved.Id,
            new JsonObject
            {
                ["taskId"] = moved.Id.ToString(),
                ["status"] = command.Status,
                ["position"] = position,
            },
            payload,
            null,
            cancellationToken).ConfigureAwait(false);

        return payload;
    }
}

/// <summary>Запрос на удаление задачи вместе с подзадачами.</summary>
/// <param name="TaskId">Идентификатор задачи.</param>
public sealed record DeleteTaskCascadeCommand(string? TaskId = null) : ICommand;

/// <summary>
/// Удаляет задачу и всё её поддерево.
/// </summary>
/// <remarks>
/// Опасное действие: хост обязан спросить подтверждение до вызова команды.
/// </remarks>
[Command(
    "delete_task_cascade",
    Description = "Удаляет задачу вместе со всеми подзадачами",
    Category = CommandCategory.Dangerous,
    RequiresConfirmation = true)]
public sealed class DeleteTaskCascadeHandler : ICommandHandler<DeleteTaskCascadeCommand>
{
    private readonly ITaskStore _store;

    /// <summary>
    /// Создаёт обработчик каскадного удаления.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    public DeleteTaskCascadeHandler(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        DeleteTaskCascadeCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await TaskCommands
            .DeleteAsync(_store, command.TaskId, TaskDeleteMode.Cascade, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Запрос на удаление задачи с отвязыванием подзадач.</summary>
/// <param name="TaskId">Идентификатор задачи.</param>
public sealed record DeleteTaskDetachCommand(string? TaskId = null) : ICommand;

/// <summary>
/// Удаляет задачу, оставляя прямых детей в том же дереве без родителя.
/// </summary>
/// <remarks>
/// Опасное действие: хост обязан спросить подтверждение до вызова команды.
/// </remarks>
[Command(
    "delete_task_detach",
    Description = "Удаляет задачу, оставляя подзадачи без родителя",
    Category = CommandCategory.Dangerous,
    RequiresConfirmation = true)]
public sealed class DeleteTaskDetachHandler : ICommandHandler<DeleteTaskDetachCommand>
{
    private readonly ITaskStore _store;

    /// <summary>
    /// Создаёт обработчик удаления с отвязыванием.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    public DeleteTaskDetachHandler(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        DeleteTaskDetachCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await TaskCommands
            .DeleteAsync(_store, command.TaskId, TaskDeleteMode.Detach, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Общая часть команд удаления: ответ и тексты ошибок у них одинаковые.</summary>
public static class TaskCommands
{
    /// <summary>Текст отказа, когда идентификатор не разобран.</summary>
    public const string MissingTaskIdError = "Не указан идентификатор задачи.";

    /// <summary>Удаляет задачу и собирает ответ хосту.</summary>
    internal static async Task<JsonObject> DeleteAsync(
        ITaskStore store,
        string? rawId,
        TaskDeleteMode mode,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(rawId, out Guid id))
        {
            return new JsonObject { ["error"] = MissingTaskIdError };
        }

        TaskDeleteResult result = await store.DeleteAsync(id, mode, cancellationToken).ConfigureAwait(false);

        if (!result.Deleted)
        {
            return new JsonObject { ["error"] = $"Задача {id} не найдена." };
        }

        return new JsonObject
        {
            ["taskId"] = id.ToString(),
            ["deleted"] = true,
            ["mode"] = TaskDeleteModes.Name(mode),
            ["removedSubtasks"] = result.RemovedSubtasks,
        };
    }
}
