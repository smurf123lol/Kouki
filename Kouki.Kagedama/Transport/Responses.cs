using System.Text.Json.Nodes;

namespace Kouki.Kagedama.Transport;

/// <summary>Задача в ответах хостов (HTTP и MCP).</summary>
/// <remarks>
/// Поля <c>workspace</c> здесь нет намеренно: колонка в базе есть, но её никто не заполняет,
/// а «всегда null» в контракте — это ложное обещание. Когда воркспейсы появятся по-настоящему,
/// поле вернётся вместе с тем, кто его пишет.
/// </remarks>
public sealed record TaskDto(
    Guid Id,
    string CreatedAt,
    string UpdatedAt,
    string Source,
    string RawText,
    string Title,
    string Status,
    int Priority,
    JsonNode? Meta);

/// <summary>Результат распознавания в ответах хостов.</summary>
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

/// <summary>Запись журнала распознавания в ответах хостов.</summary>
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
/// <remarks>
/// Поля <c>error</c> нет: неудачные распознавания и отказы отдаются кодами
/// (400/413/415/422/503) с объектом <c>{ error = "…" }</c>. Держать ещё одно поле ошибки
/// в успешном ответе значило бы описывать состояние, которое в нём не бывает.
/// </remarks>
/// <param name="Transcript">Что услышало распознавание.</param>
/// <param name="Confidence">Уверенность распознавания речи.</param>
/// <param name="DurationSec">Длительность записи.</param>
/// <param name="AudioPath">Куда сохранено аудио, если сохраняли.</param>
/// <param name="Intake">Что понял приём задач.</param>
public sealed record VoiceResponse(
    string? Transcript,
    double? Confidence,
    double DurationSec,
    string? AudioPath,
    IntakeResponse Intake);
