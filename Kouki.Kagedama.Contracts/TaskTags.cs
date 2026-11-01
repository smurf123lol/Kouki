namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Источники тегов задачи.
/// </summary>
public static class TagSources
{
    /// <summary>Тег вырезан из самой фразы (<c>#работа</c>, <c>[срочно]</c>).</summary>
    public const string Text = "text";

    /// <summary>Тег предложила модель интентов.</summary>
    public const string Model = "model";
}

/// <summary>
/// Теги задач: отдельная таблица, а не поле в мете, чтобы по ним можно было фильтровать SQL-ом.
/// </summary>
public interface ITagStore
{
    /// <summary>
    /// Добавляет теги задаче. Повторы и уже существующие теги молча пропускаются.
    /// </summary>
    /// <param name="taskId">Идентификатор задачи.</param>
    /// <param name="tags">Теги в канонической форме (без решётки).</param>
    /// <param name="source">Источник: <see cref="TagSources"/>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task AddAsync(Guid taskId, IEnumerable<string> tags, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Теги одной задачи в порядке добавления.
    /// </summary>
    /// <param name="taskId">Идентификатор задачи.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<string>> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Задачи с указанным тегом, новые сверху.
    /// </summary>
    /// <remarks>
    /// Статус фильтруется тем же запросом, что и тег: если сперва отсечь <paramref name="limit"/>,
    /// а потом отбросить чужие статусы, выборка «тег + статус + предел» потеряет подходящие задачи.
    /// </remarks>
    /// <param name="tag">Тег в канонической форме.</param>
    /// <param name="status">Статус задачи; <c>null</c> — любой статус.</param>
    /// <param name="limit">Сколько вернуть.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<Guid>> FindTasksAsync(
        string tag,
        string? status,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Теги сразу для многих задач — одним запросом.
    /// </summary>
    /// <remarks>
    /// Список задач показывает до 200 строк и обновляется дашбордом каждые пять секунд:
    /// чтение тегов по одной задаче дало бы N+1 запрос на каждую перерисовку.
    /// </remarks>
    /// <param name="ids">Идентификаторы задач.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Теги по задаче; задач без тегов в словаре нет.</returns>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ListForTasksAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);
}
