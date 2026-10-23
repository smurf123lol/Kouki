namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Статусы задачи.
/// </summary>
public static class TaskStatuses
{
    /// <summary>Задача принята, уверенность распознавания достаточная.</summary>
    public const string New = "new";

    /// <summary>Задача в работе.</summary>
    public const string InProgress = "in_progress";

    /// <summary>Задача выполнена.</summary>
    public const string Done = "done";

    /// <summary>Задача отменена.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Распознавание не уверено: нужен человек.</summary>
    public const string NeedsReview = "needs_review";

    /// <summary>Все допустимые статусы.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { New, InProgress, Done, Cancelled, NeedsReview };

    /// <summary>
    /// Проверяет, что статус известен.
    /// </summary>
    /// <param name="status">Проверяемое значение.</param>
    public static bool IsKnown(string? status) => status is not null && All.Contains(status);
}

/// <summary>
/// Задача, пришедшая с телефона, станции, дашборда или агента.
/// </summary>
public sealed class TaskRecord
{
    /// <summary>Идентификатор.</summary>
    public Guid Id { get; set; }

    /// <summary>Момент создания (ISO-8601).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>Момент последнего изменения (ISO-8601).</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>Источник: telegram, voice, dashboard, mcp, api.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Исходный текст запроса.</summary>
    public string RawText { get; set; } = string.Empty;

    /// <summary>Короткий заголовок для списков.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Статус, см. <see cref="TaskStatuses"/>.</summary>
    public string Status { get; set; } = TaskStatuses.New;

    /// <summary>Приоритет: 1 — высокий, 2 — обычный, 3 — низкий.</summary>
    public int Priority { get; set; } = 2;

    /// <summary>Рабочий каталог задачи, если он уже выделен.</summary>
    public string? Workspace { get; set; }

    /// <summary>Служебные данные: интент, уверенность, команда, ошибка.</summary>
    public string? MetaJson { get; set; }
}

/// <summary>
/// Хранилище задач.
/// </summary>
public interface ITaskStore
{
    /// <summary>
    /// Список задач, новые сверху.
    /// </summary>
    /// <param name="status">Фильтр по статусу или null.</param>
    /// <param name="limit">Сколько вернуть.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<TaskRecord>> ListAsync(string? status, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Одна задача по идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TaskRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохраняет новую задачу.
    /// </summary>
    /// <param name="record">Запись.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task AddAsync(TaskRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Частично обновляет задачу.
    /// </summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="status">Новый статус или null.</param>
    /// <param name="title">Новый заголовок или null.</param>
    /// <param name="priority">Новый приоритет или null.</param>
    /// <param name="metaJson">Новая мета или null, чтобы оставить прежнюю.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Найдена ли задача.</returns>
    Task<bool> UpdateAsync(
        Guid id,
        string? status,
        string? title,
        int? priority,
        string? metaJson = null,
        CancellationToken cancellationToken = default);
}
