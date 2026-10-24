using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.Dispatcher;
using Kouki.Shimi.Context;
using Kouki.Yasabi;

namespace Kouki.Kagedama.Mcp;

/// <summary>
/// Инструменты Kouki для MCP-клиентов (DSH-плагин, дашборд, агент).
/// </summary>
/// <remarks>
/// По решению владельца проекта через MCP доступны только запросы к локальному
/// окружению — задачи и журнал разбора; сами команды исполняет сервер,
/// а инструменты лишь вызывают его пайплайн.
/// </remarks>
public sealed class KoukiMcpTools
{
    /// <summary>
    /// Настройки сериализации ответов: кириллица без \u-эскейпов, camelCase.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly TaskIntakeService _intake;
    private readonly IRouter _router;
    private readonly IContextRegistry _contexts;
    private readonly ITaskStore _store;
    private readonly IClassificationJournal _journal;
    private readonly IActionLog _actions;

    /// <summary>
    /// Создаёт набор инструментов.
    /// </summary>
    /// <param name="intake">Приём задач: распознаёт интент и отдаёт команду диспатчеру.</param>
    /// <param name="router">Маршрутизатор команд.</param>
    /// <param name="contexts">Реестр контекстов исполнения.</param>
    /// <param name="store">Хранилище задач.</param>
    /// <param name="journal">Журнал классификаций.</param>
    /// <param name="actions">Журнал действий: история задачи и отладочная информация.</param>
    public KoukiMcpTools(
        TaskIntakeService intake,
        IRouter router,
        IContextRegistry contexts,
        ITaskStore store,
        IClassificationJournal journal,
        IActionLog actions)
    {
        _intake = intake ?? throw new ArgumentNullException(nameof(intake));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    /// <summary>
    /// Описание инструментов для <c>tools/list</c>.
    /// </summary>
    public static IReadOnlyList<McpToolDescriptor> Tools { get; } = new[]
    {
        new McpToolDescriptor(
            "kouki_add_task",
            "Создаёт задачу из свободной фразы: классифицирует её, а если фраза оказалась командой («покажи задачи») — исполняет команду, не создавая задачу.",
            Schema(
                new[] { ("text", "string", "Фраза человека, например «запиши задачу купить хлеб».") },
                required: "text")),
        new McpToolDescriptor(
            "kouki_list_tasks",
            "Показывает последние задачи Kouki.",
            Schema(
                new[]
                {
                    ("status", "string", "Фильтр: new, in_progress, done, cancelled, needs_review."),
                    ("limit", "integer", "Сколько задач вернуть, по умолчанию 10, максимум 100."),
                })),
        new McpToolDescriptor(
            "kouki_complete_task",
            "Закрывает задачу. Без taskId закрывается последняя открытая задача.",
            Schema(new[] { ("taskId", "string", "Идентификатор задачи (GUID).") })),
        new McpToolDescriptor(
            "kouki_unresolved_classifications",
            "Показывает записи журнала, которые ждут ручного разбора (low confidence и ошибки).",
            Schema(new[] { ("limit", "integer", "Сколько записей вернуть, по умолчанию 20, максимум 200.") })),
        new McpToolDescriptor(
            "kouki_task_history",
            "История действий по задаче: что распознали, какая команда исполнялась, что вернулось, сколько заняло.",
            Schema(
                new[]
                {
                    ("taskId", "string", "Идентификатор задачи (GUID)."),
                    ("limit", "integer", "Сколько действий вернуть, по умолчанию 20, максимум 200."),
                },
                required: "taskId")),
        new McpToolDescriptor(
            "kouki_label_classification",
            "Помечает запись журнала правильным интентом — из этого набирается датасет для будущей модели.",
            Schema(
                new[]
                {
                    ("id", "string", "Идентификатор записи журнала (GUID)."),
                    ("label", "string", "Правильный интент, например add_task."),
                },
                required: "id, label")),
        new McpToolDescriptor(
            "kouki_context",
            "Показывает, в каком окружении исполняются команды: имя контекста и объявленные сервисы (файловая система, терминал, хранилище состояния).",
            Schema(Array.Empty<(string, string, string)>())),
    };

    /// <summary>
    /// Возвращает описание инструмента по имени или <c>null</c>.
    /// </summary>
    /// <param name="name">Имя инструмента.</param>
    public static McpToolDescriptor? Find(string name)
    {
        foreach (McpToolDescriptor tool in Tools)
        {
            if (string.Equals(tool.Name, name, StringComparison.Ordinal))
            {
                return tool;
            }
        }

        return null;
    }

    /// <summary>
    /// Исполняет инструмент и возвращает результат в виде JSON-текста.
    /// </summary>
    /// <param name="name">Имя инструмента.</param>
    /// <param name="arguments">Аргументы вызова.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<string> InvokeAsync(string name, JsonObject? arguments, CancellationToken cancellationToken = default)
    {
        JsonObject args = arguments ?? new JsonObject();

        JsonNode result = name switch
        {
            "kouki_add_task" => await AddTaskAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_list_tasks" => await ListTasksAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_complete_task" => await CompleteTaskAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_task_history" => await TaskHistoryAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_unresolved_classifications" => await UnresolvedAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_label_classification" => await LabelAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_context" => Context(),
            _ => throw new ArgumentException($"Unknown tool '{name}'.", nameof(name)),
        };

        // Команды сообщают о проблеме полем error, а не исключением: для агента это
        // такой же отказ инструмента, как и неизвестный id, — помечаем isError.
        if (result is JsonObject payload && payload["error"] is JsonNode error && error.GetValueKind() == JsonValueKind.String)
        {
            throw new InvalidOperationException(error.GetValue<string>());
        }

        return result.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// Окружение исполнения: имя контекста и сервисы, которые он объявляет.
    /// Агент может проверить, есть ли в этом окружении терминал или файловая система,
    /// прежде чем просить команду, которой они нужны.
    /// </summary>
    private JsonNode Context()
    {
        IExecutionContext? context = _contexts.GetDefaultContext();

        if (context is null)
        {
            return new JsonObject
            {
                ["name"] = null,
                ["services"] = new JsonArray(),
                ["hint"] = "Контекст по умолчанию не задан.",
            };
        }

        var services = new JsonArray();
        foreach (string name in context.DescribeServices())
        {
            services.Add(name);
        }

        return new JsonObject
        {
            ["name"] = context.Name,
            ["services"] = services,
            ["fileSystem"] = context.FileSystem.GetType().Name,
            ["terminal"] = context.Terminal.GetType().Name,
            ["stateStore"] = context.StateStore.GetType().Name,
            ["hasServiceProvider"] = context.Services is not null,
        };
    }

    private async Task<JsonNode> AddTaskAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string text = RequiredString(args, "text");
        IntakeResult result = await _intake.IntakeAsync(text, "mcp", cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            // Фраза может оказаться командой («покажи задачи») — тогда задача не создаётся.
            ["actionId"] = result.ActionId is null ? null : result.ActionId.Value.ToString(),
            ["task"] = result.Task is null
                ? null
                : new JsonObject
                {
                    ["id"] = result.Task.Id.ToString(),
                    ["title"] = result.Task.Title,
                    ["status"] = result.Task.Status,
                },
            ["intent"] = new JsonObject
            {
                ["intent"] = result.Route.Intent.Intent,
                ["score"] = result.Route.Intent.Score,
                ["model"] = result.Route.Intent.Model,
            },
            ["command"] = result.Route.CommandName,
            ["executed"] = result.AutoExecuted,
            ["outcome"] = result.Outcome,
            ["error"] = result.Route.Error,
            ["result"] = result.Route.Result?.DeepClone(),
        };
    }

    private async Task<JsonNode> ListTasksAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var slots = new JsonObject();
        if (OptionalString(args, "status") is { } status)
        {
            slots["status"] = status;
        }

        if (OptionalInt(args, "limit") is { } limit)
        {
            slots["limit"] = limit;
        }

        return await _router.RouteAsync("list_tasks", slots, _contexts.GetDefaultContext(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonNode> CompleteTaskAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string? taskId = OptionalString(args, "taskId");

        if (string.IsNullOrWhiteSpace(taskId))
        {
            TaskRecord? open = await FindLatestOpenAsync(cancellationToken).ConfigureAwait(false);
            if (open is null)
            {
                return new JsonObject { ["error"] = "Открытых задач нет: закрывать нечего." };
            }

            taskId = open.Id.ToString();
        }

        var slots = new JsonObject { ["taskId"] = taskId };

        return await _router.RouteAsync("complete_task", slots, _contexts.GetDefaultContext(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonNode> TaskHistoryAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string rawId = RequiredString(args, "taskId");

        if (!Guid.TryParse(rawId, out Guid taskId))
        {
            return new JsonObject { ["error"] = $"'{rawId}' — не идентификатор задачи." };
        }

        int limit = Math.Clamp(OptionalInt(args, "limit") ?? 20, 1, 200);
        IReadOnlyList<ActionRecord> records = await _actions.ListAsync(limit, taskId, cancellationToken)
            .ConfigureAwait(false);

        var array = new JsonArray();
        foreach (ActionRecord record in records)
        {
            array.Add(new JsonObject
            {
                ["id"] = record.Id.ToString(),
                ["createdAt"] = record.CreatedAt,
                ["kind"] = record.Kind,
                ["source"] = record.Source,
                ["intent"] = record.Intent,
                ["command"] = record.Command,
                ["outcome"] = record.Outcome,
                ["classificationId"] = record.ClassificationId?.ToString(),
                ["args"] = Parse(record.ArgsJson),
                ["result"] = Parse(record.ResultJson),
                ["error"] = record.Error,
                ["durationMs"] = record.DurationMs,
            });
        }

        return new JsonObject { ["taskId"] = taskId.ToString(), ["count"] = array.Count, ["actions"] = array };
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private async Task<JsonNode> UnresolvedAsync(JsonObject args, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(OptionalInt(args, "limit") ?? 20, 1, 200);
        IReadOnlyList<ClassificationEntry> entries = await _journal.ListAsync(limit, onlyUnresolved: true, cancellationToken)
            .ConfigureAwait(false);

        var array = new JsonArray();
        foreach (ClassificationEntry entry in entries)
        {
            array.Add(new JsonObject
            {
                ["id"] = entry.Id.ToString(),
                ["createdAt"] = entry.CreatedAt,
                ["source"] = entry.Source,
                ["input"] = entry.InputText,
                ["intent"] = entry.Intent,
                ["score"] = entry.Score,
                ["model"] = entry.Model,
                ["confident"] = entry.Confident,
                ["error"] = entry.Error,
            });
        }

        return new JsonObject { ["count"] = array.Count, ["entries"] = array };
    }

    private async Task<JsonNode> LabelAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string rawId = RequiredString(args, "id");
        string label = RequiredString(args, "label");

        if (!Guid.TryParse(rawId, out Guid id))
        {
            return new JsonObject { ["labeled"] = false, ["error"] = $"'{rawId}' — не идентификатор записи." };
        }

        bool labeled = await _journal.LabelAsync(id, label, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["labeled"] = labeled,
            ["error"] = labeled ? null : $"Запись {id} не найдена.",
        };
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

    private static string RequiredString(JsonObject args, string name)
    {
        string? value = OptionalString(args, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Argument '{name}' is required.", nameof(args))
            : value.Trim();
    }

    private static string? OptionalString(JsonObject args, string name)
        => args[name] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static int? OptionalInt(JsonObject args, string name)
        => args[name] is JsonValue value && value.TryGetValue<int>(out int number) ? number : null;

    private static JsonObject Schema((string Name, string Type, string Description)[] properties, string? required = null)
    {
        var props = new JsonObject();
        foreach ((string name, string type, string description) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };

        if (!string.IsNullOrWhiteSpace(required))
        {
            var names = new JsonArray();
            foreach (string name in required!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                names.Add(name);
            }

            schema["required"] = names;
        }

        return schema;
    }
}

/// <summary>
/// Описание MCP-инструмента.
/// </summary>
/// <param name="Name">Имя инструмента.</param>
/// <param name="Description">Что делает инструмент.</param>
/// <param name="InputSchema">JSON-схема аргументов.</param>
public sealed record McpToolDescriptor(string Name, string Description, JsonObject InputSchema);
