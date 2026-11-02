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

    /// <summary>
    /// Открыта ли задача: новая или в работе.
    /// </summary>
    /// <remarks>
    /// «Открытость» — одно правило на весь домен: по нему ищется задача для
    /// <c>complete_task</c>, по нему же считается, что показывать в списке «мои задачи».
    /// </remarks>
    /// <param name="status">Статус задачи.</param>
    public static bool IsOpen(string? status) => status is New or InProgress;
}

/// <summary>
/// Приоритет задачи: 1 — низкий, 2 — обычный, 3 — высокий.
/// </summary>
/// <remarks>
/// Раньше число 2 было литералом в фабрике, миграции и валидации. Один словарь
/// с границами избавляет от расхождений.
/// </remarks>
public static class TaskPriorities
{
    /// <summary>Низкий приоритет.</summary>
    public const int Low = 1;

    /// <summary>Обычный приоритет (по умолчанию).</summary>
    public const int Default = 2;

    /// <summary>Высокий приоритет.</summary>
    public const int High = 3;

    /// <summary>Минимально допустимый приоритет.</summary>
    public const int Min = Low;

    /// <summary>Максимально допустимый приоритет.</summary>
    public const int Max = High;

    /// <summary>Проверяет, что приоритет в допустимых границах.</summary>
    /// <param name="priority">Проверяемое значение.</param>
    public static bool IsValid(int priority) => priority is >= Min and <= Max;
}

/// <summary>
/// Источники фразы. Значения стабильны: на них опираются журналы и мета задач,
/// поэтому новые источники добавляются только здесь.
/// </summary>
public static class TaskSources
{
    /// <summary>HTTP API (дашборд, внешние клиенты).</summary>
    public const string Api = "api";

    /// <summary>Telegram-бот.</summary>
    public const string Telegram = "telegram";

    /// <summary>Голосовой приём: запись от станции или другого клиента.</summary>
    public const string Voice = "voice";

    /// <summary>Станция Kouki (ESP32).</summary>
    public const string Station = "station";

    /// <summary>Веб-дашборд.</summary>
    public const string Dashboard = "dashboard";

    /// <summary>MCP-сервер для агента.</summary>
    public const string Mcp = "mcp";

    /// <summary>Команда, вызванная напрямую — без приёма фразы.</summary>
    public const string Command = "command";

    /// <summary>Все известные источники.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Api, Telegram, Voice, Station, Dashboard, Mcp, Command };

    /// <summary>
    /// Проверяет, что источник известен.
    /// </summary>
    /// <param name="source">Проверяемое значение.</param>
    public static bool IsKnown(string? source)
        => source is not null && All.Contains(source, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Родительская задача или null у корня.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Дерево задачи: у корня совпадает с <see cref="Id"/>.</summary>
    public Guid TreeId { get; set; }

    /// <summary>Порядок внутри пары (статус, родитель), см. <see cref="TaskPositions"/>.</summary>
    public int Position { get; set; }
}

/// <summary>
/// Порядок карточек внутри одной пары (статус, родитель).
/// </summary>
public static class TaskPositions
{
    /// <summary>Позиция первой задачи в пустой колонке.</summary>
    public const int First = 0;
}

/// <summary>
/// Как удалять задачу: вместе с потомками или оставив их без родителя.
/// </summary>
public enum TaskDeleteMode
{
    /// <summary>Удалить задачу и всё поддерево по <c>parent_id</c>.</summary>
    Cascade,

    /// <summary>Удалить только задачу: прямые дети становятся верхнеуровневыми в том же дереве.</summary>
    Detach,
}

/// <summary>
/// Имена режимов удаления для внешнего контракта (HTTP, MCP).
/// </summary>
public static class TaskDeleteModes
{
    /// <summary>Удаление вместе с поддеревом.</summary>
    public const string Cascade = "cascade";

    /// <summary>Удаление с отвязыванием прямых детей.</summary>
    public const string Detach = "detach";

    /// <summary>Режим пришёл в допустимом виде.</summary>
    public static bool IsKnown(string? mode)
        => string.Equals(mode, Cascade, StringComparison.OrdinalIgnoreCase)
           || string.Equals(mode, Detach, StringComparison.OrdinalIgnoreCase);

    /// <summary>Разбирает имя режима; неизвестное значение даёт <see cref="TaskDeleteMode.Cascade"/>.</summary>
    public static TaskDeleteMode Parse(string? mode)
        => string.Equals(mode, Detach, StringComparison.OrdinalIgnoreCase)
            ? TaskDeleteMode.Detach
            : TaskDeleteMode.Cascade;

    /// <summary>Имя режима для ответа.</summary>
    public static string Name(TaskDeleteMode mode)
        => mode == TaskDeleteMode.Detach ? Detach : Cascade;
}

/// <summary>
/// Итог удаления задачи.
/// </summary>
/// <param name="Deleted">Задача была и удалена.</param>
/// <param name="RemovedSubtasks">
/// Сколько подзадач затронуто: при <see cref="TaskDeleteMode.Cascade"/> — удалено потомков,
/// при <see cref="TaskDeleteMode.Detach"/> — отвязано прямых детей.
/// </param>
public sealed record TaskDeleteResult(bool Deleted, int RemovedSubtasks)
{
    /// <summary>Задачи с таким идентификатором нет.</summary>
    public static TaskDeleteResult NotFound { get; } = new(false, 0);
}

/// <summary>
/// Счётчики прямых подзадач одной задачи для карточки доски.
/// </summary>
/// <param name="Total">Сколько прямых детей.</param>
/// <param name="Done">Сколько из них в статусе <see cref="TaskStatuses.Done"/>.</param>
public readonly record struct SubtaskCounts(int Total, int Done);

/// <summary>
/// Хранилище задач.
/// </summary>
public interface ITaskStore
{
    /// <summary>
    /// Список задач: сначала по <see cref="TaskRecord.Position"/>, затем новые сверху.
    /// </summary>
    /// <remarks>
    /// Порядок — <c>position ASC, created_at DESC, id ASC</c>: позицию задаёт доска,
    /// а при её равенстве (старые базы, гонка вставок) сортировка остаётся устойчивой.
    /// </remarks>
    /// <param name="status">Фильтр по статусу или null.</param>
    /// <param name="limit">Сколько вернуть.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <param name="topLevel">true — только корни (<c>parent_id IS NULL</c>), для доски.</param>
    /// <param name="parentId">Только прямые дети этой задачи.</param>
    Task<IReadOnlyList<TaskRecord>> ListAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken = default,
        bool topLevel = false,
        Guid? parentId = null);

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

    /// <summary>
    /// Удаляет задачу и приводит в порядок ссылки на неё.
    /// </summary>
    /// <remarks>
    /// В одной транзакции: напоминания удаляются, ссылки в действиях и классификациях
    /// обнуляются (это аудит и датасет), теги уходят каскадом, пустое дерево удаляется.
    /// </remarks>
    /// <param name="id">Идентификатор задачи.</param>
    /// <param name="mode">Каскад или отвязывание детей.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TaskDeleteResult> DeleteAsync(
        Guid id,
        TaskDeleteMode mode = TaskDeleteMode.Cascade,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Переносит задачу в другую колонку или на другую позицию.
    /// </summary>
    /// <remarks>
    /// Сдвиг соседей и смена полей — одна транзакция: иначе доска на мгновение увидит
    /// две задачи с одной позицией. Остальные поля (заголовок, приоритет, родитель,
    /// дерево) не меняются.
    /// </remarks>
    /// <param name="id">Идентификатор перемещаемой задачи.</param>
    /// <param name="status">Целевой статус.</param>
    /// <param name="position">Желаемая позиция в колонке (0 — начало).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Перемещённая задача или null, если задачи нет.</returns>
    Task<TaskRecord?> MoveAsync(
        Guid id,
        string status,
        int position,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Счётчики прямых подзадач для набора задач — одним запросом.
    /// </summary>
    /// <remarks>
    /// Нужны доске: без агрегата на каждую карточку пришлось бы делать отдельный запрос
    /// (N+1), а список задач — самый частый запрос дашборда.
    /// </remarks>
    /// <param name="ids">Идентификаторы задач-родителей.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Счётчики по тем родителям, у которых есть подзадачи.</returns>
    Task<IReadOnlyDictionary<Guid, SubtaskCounts>> CountSubtasksAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);
}
