namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Состояния жизненного цикла напоминания.
/// </summary>
/// <remarks>
/// Напоминание — не задача: у него свой путь «срок настал → отправлено → подтверждено»,
/// и статусы задач (<see cref="TaskStatuses"/>) сюда не подходят.
/// </remarks>
public static class ReminderStates
{
    /// <summary>Срок ещё не настал.</summary>
    public const string Pending = "pending";

    /// <summary>Отправлено каналом, ждёт реакции.</summary>
    public const string Sent = "sent";

    /// <summary>Человек подтвердил («готово»).</summary>
    public const string Acknowledged = "acknowledged";

    /// <summary>Отменено до срока.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Напоминание ещё живо: ждёт срока или реакции.</summary>
    /// <param name="state">Состояние.</param>
    public static bool IsOpen(string? state) =>
        string.Equals(state, Pending, StringComparison.Ordinal)
        || string.Equals(state, Sent, StringComparison.Ordinal);
}

/// <summary>
/// Напоминание: текст, срок и состояние доставки.
/// </summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Text">О чём напомнить.</param>
/// <param name="DueAt">Когда напомнить (UTC).</param>
/// <param name="Repeat">Повтор: <c>null</c>, <c>daily</c> или <c>weekly</c>.</param>
/// <param name="State">Состояние из <see cref="ReminderStates"/>.</param>
/// <param name="Source">Откуда пришло: станция, бот, api.</param>
/// <param name="TaskId">Карточка задачи, если напоминание привязано к задаче.</param>
/// <param name="CreatedAt">Когда создано.</param>
/// <param name="SentAt">Когда отправлено.</param>
/// <param name="AcknowledgedAt">Когда подтверждено.</param>
public sealed record ReminderRecord(
    Guid Id,
    string Text,
    DateTimeOffset DueAt,
    string? Repeat,
    string State,
    string Source,
    Guid? TaskId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? AcknowledgedAt);

/// <summary>
/// Хранилище напоминаний.
/// </summary>
public interface IReminderStore
{
    /// <summary>
    /// Создаёт напоминание.
    /// </summary>
    /// <param name="text">О чём напомнить.</param>
    /// <param name="dueAt">Когда (UTC).</param>
    /// <param name="repeat">Повтор или <c>null</c>.</param>
    /// <param name="source">Источник.</param>
    /// <param name="taskId">Карточка задачи или <c>null</c>.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ReminderRecord> AddAsync(
        string text,
        DateTimeOffset dueAt,
        string? repeat,
        string source,
        Guid? taskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Перечисляет напоминания от ближайшего срока.
    /// </summary>
    /// <param name="openOnly">Только живые (ожидают срока или реакции).</param>
    /// <param name="limit">Сколько вернуть (1…500).</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<IReadOnlyList<ReminderRecord>> ListAsync(
        bool openOnly,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Забирает созревшие напоминания и помечает их отправленными.
    /// </summary>
    /// <remarks>
    /// Помечает <c>sent</c> в той же транзакции, что и чтение: иначе два канала
    /// (бот и станция) отправят одно напоминание дважды.
    /// </remarks>
    /// <param name="now">Текущее время (UTC).</param>
    /// <param name="limit">Сколько забрать.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<IReadOnlyList<ReminderRecord>> DueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Отменяет напоминание.
    /// </summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ReminderRecord?> CancelAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Подтверждает получение напоминания.
    /// </summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ReminderRecord?> AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Откладывает напоминание на указанный промежуток.
    /// </summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="delay">На сколько отложить (положительный промежуток).</param>
    /// <param name="now">Текущее время (UTC): от него считается новый срок.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ReminderRecord?> SnoozeAsync(
        Guid id,
        TimeSpan delay,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Привязывает напоминание к карточке задачи (двусторонняя ссылка).
    /// </summary>
    /// <param name="reminderId">Напоминание.</param>
    /// <param name="taskId">Задача.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task LinkTaskAsync(Guid reminderId, Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Напоминания, привязанные к задаче.
    /// </summary>
    /// <param name="taskId">Задача.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<IReadOnlyList<ReminderRecord>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default);
}
