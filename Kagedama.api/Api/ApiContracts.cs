using System.Text.Json;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Data;
using Kouki.Yasabi;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Api.Api;

/// <summary>Запрос на приём фразы.</summary>
/// <param name="Text">Текст от человека.</param>
/// <param name="Source">Источник: telegram, voice, dashboard, mcp, api. По умолчанию api.</param>
public sealed record IntakeRequest(string Text, string? Source = null, string? Intent = null);

/// <summary>Запрос на сравнение классификаторов: одна фраза, все «роутеры».</summary>
/// <param name="Text">Фраза.</param>
/// <param name="Classifiers">Имена классификаторов; <c>null</c> — все зарегистрированные.</param>
public sealed record ClassifyRequest(string Text, List<string>? Classifiers = null);

/// <summary>Задача в ответах API.</summary>
public sealed record TaskDto(
    Guid Id,
    string CreatedAt,
    string UpdatedAt,
    string Source,
    string RawText,
    string Title,
    string Status,
    int Priority,
    string? Workspace,
    JsonNode? Meta);

/// <summary>Результат распознавания в ответах API.</summary>
public sealed record IntentDto(string Intent, double Score, string Model, bool Confident);

/// <summary>Ответ на приём фразы.</summary>
/// <param name="Task">Созданная задача; <c>null</c>, если фраза была командой.</param>
/// <param name="Intent">Вердикт распознавания.</param>
/// <param name="Command">Имя исполненной команды, если была.</param>
/// <param name="Executed">Исполнена ли команда.</param>
/// <param name="Outcome">Итог: created / executed / needs_review / failed.</param>
/// <param name="Error">Текст ошибки, если была.</param>
/// <param name="Result">Ответ команды — чтобы бот и дашборд показали, что именно вернулось.</param>
public sealed record IntakeResponse(
    TaskDto? Task,
    IntentDto Intent,
    string? Command,
    bool Executed,
    string Outcome,
    string? Error,
    JsonNode? Result = null);

/// <summary>Запись журнала действий — по ней разбирают, что и почему произошло.</summary>
/// <param name="Id">Идентификатор действия.</param>
/// <param name="CreatedAt">Момент выполнения (ISO 8601).</param>
/// <param name="TaskId">Связанная задача, если действие её создало или изменило.</param>
/// <param name="ClassificationId">Запись журнала распознавания, по которой принято решение.</param>
/// <param name="Source">Источник: telegram, voice, dashboard, mcp, api.</param>
/// <param name="Kind">Вид действия: intake или command.</param>
/// <param name="Intent">Распознанный интент.</param>
/// <param name="Command">Команда, если исполнялась.</param>
/// <param name="Outcome">created, executed, needs_review или failed.</param>
/// <param name="Args">Аргументы команды, если исполнялась.</param>
/// <param name="Result">Ответ команды, если исполнялась.</param>
/// <param name="Error">Текст ошибки, если была.</param>
/// <param name="DurationMs">Сколько заняло действие.</param>
public sealed record ActionDto(
    Guid Id,
    string CreatedAt,
    Guid? TaskId,
    Guid? ClassificationId,
    string Source,
    string Kind,
    string? Intent,
    string? Command,
    string Outcome,
    JsonNode? Args,
    JsonNode? Result,
    string? Error,
    long? DurationMs);

/// <summary>Частичное обновление задачи.</summary>
public sealed record TaskPatchRequest(string? Status = null, string? Title = null, int? Priority = null);

/// <summary>Ручная разметка записи журнала.</summary>
public sealed record LabelRequest(string Label);

/// <summary>Запись журнала в ответах API.</summary>
public sealed record ClassificationDto(
    Guid Id,
    string CreatedAt,
    string Source,
    string InputKind,
    string InputText,
    string Intent,
    double Score,
    bool Confident,
    string Model,
    string? Error,
    string? Label,
    string? LabeledAt);

/// <summary>Ответ на голосовой запрос.</summary>
/// <param name="Transcript">Что услышало распознавание.</param>
/// <param name="Confidence">Уверенность распознавания речи.</param>
/// <param name="DurationSec">Длительность записи.</param>
/// <param name="AudioPath">Куда сохранено аудио, если сохраняли.</param>
/// <param name="Intake">Что понял приём задач; <c>null</c>, если речь не распознана.</param>
/// <param name="Error">Почему не получилось, если не получилось.</param>
public sealed record VoiceResponse(
    string? Transcript,
    double? Confidence,
    double DurationSec,
    string? AudioPath,
    IntakeResponse? Intake,
    string? Error = null);

/// <summary>Преобразование записей в DTO.</summary>
public static class ApiMapping
{
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
            record.Workspace,
            ParseMeta(record.MetaJson));
    }

    /// <summary>Результат распознавания → DTO.</summary>
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
