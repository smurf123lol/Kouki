using System.Text.Json.Nodes;
using Kouki.Kagedama.Data;
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
/// <param name="Source">Откуда пришла фраза: api, telegram, voice, dashboard, mcp.</param>
public sealed record AddTaskCommand(string? Text = null, string? Source = null) : ICommand;

/// <summary>
/// Создаёт задачу тем же путём, что и остальные операции: через диспатчер.
/// Приём фраз только распознаёт интент и подставляет слоты — записей в базу он не делает.
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

        string text = command.Text.Trim();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var record = new TaskRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O"),
            Source = string.IsNullOrWhiteSpace(command.Source) ? "command" : command.Source,
            RawText = text,
            Title = TaskTitles.Make(text),
            Status = TaskStatuses.New,
            Priority = 2,
            MetaJson = null,
        };

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

        int limit = Math.Clamp(command.Limit ?? 10, 1, 100);
        string? status = TaskStatuses.IsKnown(command.Status) ? command.Status : null;
        IReadOnlyList<TaskRecord> tasks = await _store.ListAsync(status, limit, cancellationToken).ConfigureAwait(false);

        var array = new JsonArray();
        foreach (TaskRecord task in tasks)
        {
            array.Add(new JsonObject
            {
                ["id"] = task.Id.ToString(),
                ["title"] = task.Title,
                ["status"] = task.Status,
                ["priority"] = task.Priority,
                ["createdAt"] = task.CreatedAt,
            });
        }

        return new JsonObject
        {
            ["count"] = array.Count,
            ["status"] = status,
            ["tasks"] = array,
        };
    }
}

/// <summary>
/// Запрос на закрытие задачи.
/// </summary>
/// <param name="TaskId">Идентификатор задачи; если не задан, хост подставляет последнюю открытую.</param>
public sealed record CompleteTaskCommand(string? TaskId = null) : ICommand;

/// <summary>
/// Закрывает задачу.
/// </summary>
[Command(
    "complete_task",
    Description = "Закрывает задачу по идентификатору",
    Category = CommandCategory.Navigation,
    RequiresConfirmation = true,
    Example = "готово, закрывай задачу")]
public sealed class CompleteTaskHandler : ICommandHandler<CompleteTaskCommand>
{
    private readonly ITaskStore _store;

    /// <summary>
    /// Создаёт обработчик закрытия задачи.
    /// </summary>
    /// <param name="store">Хранилище задач.</param>
    public CompleteTaskHandler(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        CompleteTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Guid.TryParse(command.TaskId, out Guid id))
        {
            return new JsonObject { ["error"] = "Не указан идентификатор задачи: нечего закрывать." };
        }

        TaskRecord? existing = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new JsonObject { ["error"] = $"Задача {id} не найдена." };
        }

        if (existing.Status is TaskStatuses.Done or TaskStatuses.Cancelled)
        {
            return new JsonObject
            {
                ["taskId"] = id.ToString(),
                ["status"] = existing.Status,
                ["alreadyClosed"] = true,
            };
        }

        await _store.UpdateAsync(id, TaskStatuses.Done, null, null, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["taskId"] = id.ToString(),
            ["title"] = existing.Title,
            ["status"] = TaskStatuses.Done,
        };
    }
}
