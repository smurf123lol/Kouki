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
public sealed record AddTaskCommand(string? Text = null, string? Source = null) : ICommand;

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

    /// <summary>
    /// Создаёт обработчик создания задачи.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    public AddTaskHandler(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        AddTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return new JsonObject { ["error"] = "Пустая фраза: нечего записывать как задачу." };
        }

        string source = string.IsNullOrWhiteSpace(command.Source) ? TaskSources.Command : command.Source.Trim();
        TaskRecord record = TaskFactory.Create(command.Text, source);

        await _store.AddAsync(record, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["taskId"] = record.Id.ToString(),
            ["title"] = record.Title,
            ["status"] = record.Status,
            ["created"] = true,
        };
    }
}

/// <summary>
/// Запрос списка задач.
/// </summary>
/// <param name="Status">Фильтр по статусу или null.</param>
/// <param name="Limit">Сколько показать; по умолчанию десять.</param>
public sealed record ListTasksCommand(string? Status = null, int? Limit = null) : ICommand;

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
    private readonly ITaskStore _store;

    /// <summary>
    /// Создаёт обработчик списка задач.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    public ListTasksHandler(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        ListTasksCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int limit = Math.Clamp(command.Limit ?? 10, 1, 500);
        string? status = TaskStatuses.IsKnown(command.Status) ? command.Status : null;
        IReadOnlyList<TaskRecord> tasks = await _store.ListAsync(status, limit, cancellationToken).ConfigureAwait(false);

        var array = new JsonArray();
        foreach (TaskRecord task in tasks)
        {
            array.Add(ToJson(task));
        }

        return new JsonObject
        {
            ["count"] = array.Count,
            ["status"] = status,
            ["tasks"] = array,
        };
    }

    internal static JsonObject ToJson(TaskRecord task)
        => new()
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
            ["meta"] = ParseMeta(task.MetaJson),
        };

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
            return new JsonObject { ["error"] = message };
        }

        if (existing.Status is TaskStatuses.Done or TaskStatuses.Cancelled)
        {
            return new JsonObject
            {
                ["taskId"] = existing.Id.ToString(),
                ["status"] = existing.Status,
                ["alreadyClosed"] = true,
            };
        }

        await _store.UpdateAsync(existing.Id, TaskStatuses.Done, null, null, cancellationToken: cancellationToken).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["taskId"] = existing.Id.ToString(),
            ["title"] = existing.Title,
            ["status"] = TaskStatuses.Done,
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
            return new JsonObject { ["error"] = "Не указан идентификатор задачи." };
        }

        if (command.Status is not null && !TaskStatuses.IsKnown(command.Status))
        {
            return new JsonObject { ["error"] = $"Неизвестный статус '{command.Status}'." };
        }

        if (command.Priority is int priority && !TaskPriorities.IsValid(priority))
        {
            return new JsonObject { ["error"] = $"Приоритет: {TaskPriorities.Min}, {TaskPriorities.Default} или {TaskPriorities.Max}." };
        }

        bool updated = await _store
            .UpdateAsync(id, command.Status, command.Title, command.Priority, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!updated)
        {
            return new JsonObject { ["error"] = "Задача не найдена." };
        }

        TaskRecord? task = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return new JsonObject { ["error"] = "Задача не найдена." };
        }

        JsonObject payload = ListTasksHandler.ToJson(task);

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
