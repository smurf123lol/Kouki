namespace Kouki.Kagedama.Api.Api;

/// <summary>Запрос на приём фразы.</summary>
/// <param name="Text">Текст от человека.</param>
/// <param name="Source">Источник: telegram, voice, dashboard, mcp, api. По умолчанию api.</param>
/// <param name="Intent">Подсказка сессии: add_task или ask (станция сообщает, что это запись).</param>
public sealed record IntakeRequest(string Text, string? Source = null, string? Intent = null);

/// <summary>Запрос на сравнение классификаторов: одна фраза, все «роутеры».</summary>
/// <param name="Text">Фраза.</param>
/// <param name="Classifiers">Имена классификаторов; <c>null</c> — все зарегистрированные.</param>
public sealed record ClassifyRequest(string Text, List<string>? Classifiers = null);
/// <summary>Запрос озвучивания: текст, который станция прочитает вслух.</summary>
/// <param name="Text">Фраза (до 400 символов — длиннее слушать бессмысленно).</param>
/// <param name="Voice">Имя голоса для озвучивания (опционально).</param>
public sealed record SpeechRequest(string Text, string? Voice = null);

/// <summary>Частичное обновление задачи.</summary>
public sealed record TaskPatchRequest(string? Status = null, string? Title = null, int? Priority = null);

/// <summary>Создание задачи с дашборда.</summary>
/// <param name="Text">Формулировка задачи.</param>
/// <param name="ParentId">Родительская задача, если создаётся подзадача.</param>
/// <param name="Priority">Приоритет: 1, 2 или 3; <c>null</c> — по умолчанию.</param>
public sealed record TaskCreateRequest(string? Text = null, Guid? ParentId = null, int? Priority = null);

/// <summary>Перенос карточки в колонку и на позицию.</summary>
/// <param name="Status">Целевой статус; обязателен.</param>
/// <param name="Position">Позиция в колонке, начиная с нуля.</param>
public sealed record TaskMoveRequest(string? Status = null, int? Position = null);

/// <summary>Ручная разметка записи журнала.</summary>
public sealed record LabelRequest(string Label);

/// <summary>Сводка телеметрии запросов к API.</summary>
public sealed record TelemetryResponse(
    int WindowHours,
    string Since,
    TelemetryTotalsResponse Totals,
    IReadOnlyList<TelemetryEndpointResponse> ByEndpoint,
    IReadOnlyList<TelemetrySourceResponse> BySource);

/// <summary>Итоговые показатели телеметрии.</summary>
public sealed record TelemetryTotalsResponse(
    int Requests,
    int Errors,
    double AvgDurationMs,
    long AudioBytes);

/// <summary>Показатели телеметрии по эндпоинту.</summary>
public sealed record TelemetryEndpointResponse(
    string Endpoint,
    int Requests,
    int Errors,
    double AvgDurationMs,
    long AudioBytes);

/// <summary>Показатели телеметрии по источнику.</summary>
public sealed record TelemetrySourceResponse(
    string Source,
    int Requests,
    int Errors);
