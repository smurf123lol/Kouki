using System.Text.Json;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Kouki.Yasabi;

namespace Kouki.Kagedama.Transport;

/// <summary>
/// Преобразование записей в DTO — одно на все хосты.
/// </summary>
/// <remarks>
/// Раньше HTTP собирал ответ через эти методы, а MCP-хост собирал такой же ответ руками
/// (<c>KoukiMcpTools.AddTaskAsync</c>), и формы уже начали расходиться: у MCP не было
/// <c>priority</c>, <c>source</c> и <c>meta</c>. Теперь оба хоста берут ответ отсюда.
/// </remarks>
public static class ApiMapping
{
    /// <summary>Настройки сериализации, совпадающие с настройками HTTP-хоста (camelCase).</summary>
    public static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>Любое значение → JSON-узел в форме, привычной HTTP-клиенту.</summary>
    /// <param name="value">Значение для сериализации.</param>
    public static JsonNode? ToJson<T>(T value)
        => JsonSerializer.SerializeToNode(value, WebJson);

    /// <summary>
    /// Ответ команды с одним объектом задачи → DTO.
    /// </summary>
    /// <param name="node">Объект из ответа <c>patch_task</c> или подобной команды.</param>
    public static TaskDto? FromTaskJson(JsonNode? node)
        => node is JsonObject ? node.Deserialize<TaskDto>(WebJson) : null;

    /// <summary>
    /// Ответ команды <c>list_tasks</c> → список DTO в форме, привычной HTTP-клиенту.
    /// </summary>
    /// <param name="node">Объект вида <c>{count, status, tasks[]}</c>.</param>
    public static IReadOnlyList<TaskDto> ToTaskList(JsonNode? node)
    {
        if (node is not JsonObject payload || payload["tasks"] is not JsonArray array)
        {
            return Array.Empty<TaskDto>();
        }

        var tasks = new List<TaskDto>(array.Count);

        foreach (JsonNode? item in array)
        {
            TaskDto? task = FromTaskJson(item);

            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        return tasks;
    }

    /// <summary>
    /// Ответ команды <c>list_actions</c> → список DTO журнала действий.
    /// </summary>
    /// <param name="node">Объект вида <c>{count, actions[]}</c>.</param>
    public static IReadOnlyList<ActionDto> ToActionList(JsonNode? node)
    {
        if (node is not JsonObject payload || payload["actions"] is not JsonArray array)
        {
            return Array.Empty<ActionDto>();
        }

        return array.Deserialize<List<ActionDto>>(WebJson) ?? new List<ActionDto>();
    }

    /// <summary>
    /// Ответ команды <c>list_classifications</c> → список DTO журнала разбора.
    /// </summary>
    /// <param name="node">Объект вида <c>{count, entries[]}</c>.</param>
    public static IReadOnlyList<ClassificationDto> ToClassificationList(JsonNode? node)
    {
        if (node is not JsonObject payload || payload["entries"] is not JsonArray array)
        {
            return Array.Empty<ClassificationDto>();
        }

        return array.Deserialize<List<ClassificationDto>>(WebJson) ?? new List<ClassificationDto>();
    }

    /// <summary>Задача → DTO.</summary>
    public static TaskDto ToDto(TaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new TaskDto(
            record.Id,
            record.CreatedAt,
            record.UpdatedAt,
            record.Source,
            record.RawText,
            record.Title,
            record.Status,
            record.Priority,
            ParseMeta(record.MetaJson));
    }

    /// <summary>Результат распознавания → DTO.</summary>
    /// <param name="intent">Вердикт классификатора.</param>
    /// <param name="threshold">Порог, тем же числом, что решал судьбу фразы.</param>
    public static IntentDto ToDto(IntentResult intent, double threshold)
    {
        ArgumentNullException.ThrowIfNull(intent);

        return new IntentDto(intent.Intent, intent.Score, intent.Model, intent.IsConfident(threshold));
    }

    /// <summary>Запись журнала → DTO.</summary>
    public static ClassificationDto ToDto(ClassificationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ClassificationDto(
            entry.Id,
            entry.CreatedAt,
            entry.Source,
            entry.InputKind,
            entry.InputText,
            entry.Intent,
            entry.Score,
            entry.Confident,
            entry.Model,
            entry.Error,
            entry.Label,
            entry.LabeledAt);
    }

    /// <summary>Действие → DTO.</summary>
    public static ActionDto ToDto(ActionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ActionDto(
            record.Id,
            record.CreatedAt,
            record.TaskId,
            record.ClassificationId,
            record.Source,
            record.Kind,
            record.Intent,
            record.Command,
            record.Outcome,
            ParseMeta(record.ArgsJson),
            ParseMeta(record.ResultJson),
            record.Error,
            record.DurationMs);
    }

    /// <summary>Результат приёма фразы → ответ хоста.</summary>
    /// <param name="result">Что вернул приём.</param>
    /// <param name="confidenceThreshold">Порог, по которому считалось поле <c>confident</c>.</param>
    public static IntakeResponse ToResponse(IntakeResult result, double confidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new IntakeResponse(
            result.Task is null ? null : ToDto(result.Task),
            ToDto(result.Route.Intent, confidenceThreshold),
            result.Route.CommandName,
            result.Route.Executed,
            result.Outcome,
            result.Route.Error,
            result.Route.Result);
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
        catch (JsonException)
        {
            return null;
        }
    }
}
