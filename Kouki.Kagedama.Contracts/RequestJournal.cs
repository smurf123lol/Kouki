namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Имена эндпоинтов, запросы к которым журналируются в телеметрии.
/// </summary>
public static class RequestEndpoints
{
    /// <summary>Голосовой приём (POST /api/voice).</summary>
    public const string Voice = "voice";

    /// <summary>Классификация текста (POST /api/classify).</summary>
    public const string Classify = "classify";

    /// <summary>Все известные эндпоинты телеметрии.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Voice, Classify };
}

/// <summary>
/// Исходы обработки запроса для телеметрии.
/// </summary>
public static class RequestOutcomes
{
    /// <summary>Запрос успешно обработан (2xx).</summary>
    public const string Ok = "ok";

    /// <summary>Запрос отклонён хостом по правилам валидации/приёма (4xx).</summary>
    public const string Rejected = "rejected";

    /// <summary>Запрос завершился сбоем/ошибкой сервера (5xx).</summary>
    public const string Error = "error";

    /// <summary>Все известные исходы телеметрии.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Ok, Rejected, Error };
}

/// <summary>
/// Запись журнала запросов к API: время, источник, эндпоинт, код ответа,
/// длительность и метрики аудио.
/// </summary>
/// <param name="Id">Идентификатор записи.</param>
/// <param name="CreatedAt">Момент получения запроса (UTC).</param>
/// <param name="Endpoint">Эндпоинт: см. <see cref="RequestEndpoints"/>.</param>
/// <param name="Source">Источник запроса (station, dashboard, telegram, mcp, api).</param>
/// <param name="HttpStatus">Итоговый HTTP-код ответа.</param>
/// <param name="Outcome">Итог: см. <see cref="RequestOutcomes"/>.</param>
/// <param name="DurationMs">Длительность обработки в миллисекундах.</param>
/// <param name="Intent">Распознанный интент, если известен.</param>
/// <param name="TaskId">Идентификатор связанной задачи, если создана.</param>
/// <param name="ClassificationId">Идентификатор записи журнала распознавания, если есть.</param>
/// <param name="AudioBytes">Размер переданного аудио в байтах.</param>
/// <param name="AudioDurationSec">Длительность аудио в секундах.</param>
/// <param name="Error">Человекочитаемый текст ошибки при отказе или сбое.</param>
public sealed record RequestLogEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Endpoint,
    string Source,
    int HttpStatus,
    string Outcome,
    long DurationMs,
    string? Intent = null,
    Guid? TaskId = null,
    Guid? ClassificationId = null,
    long? AudioBytes = null,
    double? AudioDurationSec = null,
    string? Error = null);

/// <summary>
/// Общие итоги телеметрии за временное окно.
/// </summary>
/// <param name="Requests">Всего запросов.</param>
/// <param name="Errors">Число запросов с ошибками или отказами (outcome != ok).</param>
/// <param name="AvgDurationMs">Среднее время ответа в миллисекундах.</param>
/// <param name="AudioBytes">Суммарный объём переданного аудио в байтах.</param>
public sealed record RequestLogTotals(
    int Requests,
    int Errors,
    double AvgDurationMs,
    long AudioBytes);

/// <summary>
/// Агрегированные метрики запросов по конкретному эндпоинту.
/// </summary>
/// <param name="Endpoint">Эндпоинт (voice или classify).</param>
/// <param name="Requests">Число запросов к эндпоинту.</param>
/// <param name="Errors">Число ошибок/отказов.</param>
/// <param name="AvgDurationMs">Среднее время ответа в миллисекундах.</param>
/// <param name="AudioBytes">Суммарный объём переданного аудио в байтах.</param>
public sealed record RequestLogEndpointSummary(
    string Endpoint,
    int Requests,
    int Errors,
    double AvgDurationMs,
    long AudioBytes);

/// <summary>
/// Агрегированные метрики запросов по источнику.
/// </summary>
/// <param name="Source">Источник (station, dashboard и т.д.).</param>
/// <param name="Requests">Число запросов от источника.</param>
/// <param name="Errors">Число ошибок/отказов.</param>
public sealed record RequestLogSourceSummary(
    string Source,
    int Requests,
    int Errors);

/// <summary>
/// Сводка телеметрии запросов за окно в часах.
/// </summary>
/// <param name="WindowHours">Размер окна в часах.</param>
/// <param name="Since">Начало окна (UTC).</param>
/// <param name="Totals">Общие показатели.</param>
/// <param name="ByEndpoint">Разбивка по эндпоинтам.</param>
/// <param name="BySource">Разбивка по источникам.</param>
public sealed record RequestLogSummary(
    int WindowHours,
    DateTimeOffset Since,
    RequestLogTotals Totals,
    IReadOnlyList<RequestLogEndpointSummary> ByEndpoint,
    IReadOnlyList<RequestLogSourceSummary> BySource);

/// <summary>
/// Журнал запросов к API и сервис телеметрии.
/// Записывает метрики обращений к /api/voice и /api/classify и считает агрегаты.
/// </summary>
public interface IRequestLog
{
    /// <summary>
    /// Записывает обращение к API. При превышении <paramref name="maxRows"/> самые старые
    /// записи запросов удаляются (кольцевой буфер).
    /// </summary>
    /// <param name="entry">Запись о запросе.</param>
    /// <param name="maxRows">Максимальное число хранящихся строк (0 — не удалять).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Идентификатор созданной записи.</returns>
    Task<Guid> RecordAsync(RequestLogEntry entry, int maxRows = 10000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет старые записи обращений сверх <paramref name="maxRows"/>.
    /// </summary>
    /// <param name="maxRows">Сколько последних записей оставить (0 — ничего не удалять).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Число удалённых строк.</returns>
    Task<int> PruneAsync(int maxRows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Считает сводку телеметрии за последние <paramref name="hours"/> часов.
    /// </summary>
    /// <param name="hours">Окно в часах (1…168).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сводные показатели телеметрии.</returns>
    Task<RequestLogSummary> SummarizeAsync(int hours, CancellationToken cancellationToken = default);
}

/// <summary>
/// Заглушка журнала запросов, когда сбор телеметрии отключён.
/// </summary>
public sealed class NullRequestLog : IRequestLog
{
    /// <summary>Единственный экземпляр.</summary>
    public static NullRequestLog Instance { get; } = new();

    /// <inheritdoc />
    public Task<Guid> RecordAsync(RequestLogEntry entry, int maxRows = 10000, CancellationToken cancellationToken = default)
        => Task.FromResult(entry.Id);

    /// <inheritdoc />
    public Task<int> PruneAsync(int maxRows, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <inheritdoc />
    public Task<RequestLogSummary> SummarizeAsync(int hours, CancellationToken cancellationToken = default)
    {
        DateTimeOffset since = DateTimeOffset.UtcNow.AddHours(-hours);
        return Task.FromResult(new RequestLogSummary(
            hours,
            since,
            new RequestLogTotals(0, 0, 0.0, 0),
            Array.Empty<RequestLogEndpointSummary>(),
            Array.Empty<RequestLogSourceSummary>()));
    }
}
