using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Transport;
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
    /// <summary>
    /// Создаёт набор инструментов.
    /// </summary>
    /// <param name="intake">Приём задач: распознаёт интент и отдаёт команду диспатчеру.</param>
    /// <param name="router">Маршрутизатор команд.</param>
    /// <param name="contexts">Реестр контекстов исполнения.</param>
    public KoukiMcpTools(
        TaskIntakeService intake,
        IRouter router,
        IContextRegistry contexts)
    {
        _intake = intake ?? throw new ArgumentNullException(nameof(intake));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
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
            "kouki_add_reminder",
            "Создаёт напоминание из фразы со временем: «напомни через 20 минут полить цветы», «завтра в 9 позвонить маме».",
            Schema(
                new[] { ("text", "string", "Фраза целиком, включая время напоминания.") },
                required: "text")),
        new McpToolDescriptor(
            "kouki_list_reminders",
            "Показывает напоминания: ожидающие или все.",
            Schema(
                new[]
                {
                    ("open", "boolean", "true — только незакрытые (по умолчанию), false — все."),
                    ("limit", "integer", "Сколько вернуть, по умолчанию 10, максимум 100."),
                })),
        new McpToolDescriptor(
            "kouki_complete_reminder",
            "Закрывает напоминание по идентификатору.",
            Schema(new[] { ("reminderId", "string", "Идентификатор напоминания (GUID).") }, required: "reminderId")),
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
            "kouki_add_reminder" => await AddReminderAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_list_reminders" => await ListRemindersAsync(args, cancellationToken).ConfigureAwait(false),
            "kouki_complete_reminder" => await CompleteReminderAsync(args, cancellationToken).ConfigureAwait(false),
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

        // Те же правила границы, что у api: лимит длины и источник по умолчанию хоста.
        // Хост — транспорт; если правила повторять здесь, они разойдутся.
        if (!IntakeRequestPolicy.TryPrepare(
                text,
                null,
                null,
                _intake.DefaultSource,
                IntakeRequestPolicy.DefaultMaxTextLength,
                out IntakeSpec? spec,
                out IntakeRejection? rejection))
        {
            throw new ArgumentException(rejection!.Message, nameof(args));
        }

        IntakeResult result = await _intake.IntakeAsync(spec!.Text, spec.Source, cancellationToken).ConfigureAwait(false);

        // Ответ собирает один и тот же маппинг, что и для HTTP: иначе поля снова разъедутся
        // (в прежней ручной сборке не было priority, source и meta).
        if (ApiMapping.ToJson(ApiMapping.ToResponse(result, _intake.ConfidenceThreshold)) is not JsonObject payload)
        {
            return new JsonObject { ["outcome"] = result.Outcome };
        }

        // Идентификатор действия — не часть HTTP-ответа, это удобство отладки для агента.
        payload["actionId"] = result.ActionId is null ? null : result.ActionId.Value.ToString();
        return payload;
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

    private async Task<JsonNode> AddReminderAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string text = RequiredString(args, "text");
        var slots = new JsonObject { ["text"] = text, ["source"] = _intake.DefaultSource };

        // Время разбирает команда (ReminderTime): у HTTP и MCP одна и та же логика,
        // а «не понял, когда» приходит обычным отказом в поле error.
        return await _router.RouteAsync("add_reminder", slots, _contexts.GetDefaultContext(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonNode> ListRemindersAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var slots = new JsonObject();

        if (args["open"] is JsonNode open && open.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            slots["openOnly"] = open.GetValue<bool>();
        }

        if (OptionalInt(args, "limit") is { } limit)
        {
            slots["limit"] = limit;
        }

        return await _router.RouteAsync("list_reminders", slots, _contexts.GetDefaultContext(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonNode> CompleteReminderAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var slots = new JsonObject { ["id"] = RequiredString(args, "reminderId") };

        return await _router.RouteAsync("complete_reminder", slots, _contexts.GetDefaultContext(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonNode> CompleteTaskAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var slots = new JsonObject();

        if (OptionalString(args, "taskId") is { Length: > 0 } taskId)
        {
            slots["taskId"] = taskId;
        }

        // Какую задачу закрывать, решает команда: у HTTP и MCP один и тот же порядок.
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

        // История задачи — тоже команда: поля и порядок те же, что у HTTP.
        JsonNode? result = await _router.RouteAsync(
            "list_actions",
            new JsonObject { ["taskId"] = taskId.ToString(), ["limit"] = limit },
            _contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload && payload["actions"] is JsonArray actions)
        {
            return new JsonObject
            {
                ["taskId"] = taskId.ToString(),
                ["count"] = actions.Count,
                ["actions"] = actions.DeepClone(),
            };
        }

        return new JsonObject
        {
            ["taskId"] = taskId.ToString(),
            ["count"] = 0,
            ["actions"] = new JsonArray(),
        };
    }
    private async Task<JsonNode> UnresolvedAsync(JsonObject args, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(OptionalInt(args, "limit") ?? 20, 1, 200);

        return await _router.RouteAsync(
            "list_classifications",
            new JsonObject { ["limit"] = limit, ["unresolved"] = true },
            _contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);
    }
    private async Task<JsonNode> LabelAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string rawId = RequiredString(args, "id");
        string label = OptionalString(args, "label") ?? string.Empty;

        if (!Guid.TryParse(rawId, out Guid id))
        {
            return new JsonObject { ["labeled"] = false, ["error"] = $"'{rawId}' — не идентификатор записи." };
        }

        // Пустую метку отбивает команда, а не хост: правило одно для HTTP и MCP.
        return await _router.RouteAsync(
            "label_classification",
            new JsonObject { ["id"] = id.ToString(), ["label"] = label },
            _contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);
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
