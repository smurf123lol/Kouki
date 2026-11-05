namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Запись вопроса пользователя и ответа модели.
/// </summary>
/// <param name="Id">Уникальный идентификатор вопроса.</param>
/// <param name="CreatedAt">Время создания в формате ISO-8601 (UTC).</param>
/// <param name="Source">Источник запроса (station, telegram, dashboard, api, mcp).</param>
/// <param name="Question">Текст вопроса.</param>
/// <param name="Answer">Текст ответа модели.</param>
/// <param name="TaskId">Связанная задача, если из вопроса была создана задача.</param>
/// <param name="Model">Имя или сигнатура модели/ответчика.</param>
/// <param name="DurationMs">Время генерации ответа в миллисекундах.</param>
/// <param name="AudioPath">Путь к сохранённому аудиофайлу запроса, если есть.</param>
public sealed record QuestionRecord(
    Guid Id,
    string CreatedAt,
    string Source,
    string Question,
    string Answer,
    Guid? TaskId = null,
    string? Model = null,
    long? DurationMs = null,
    string? AudioPath = null);

/// <summary>
/// Хранилище истории вопросов и ответов.
/// </summary>
public interface IQuestionStore
{
    /// <summary>
    /// Сохраняет вопрос и ответ в журнал.
    /// </summary>
    Task<Guid> AddAsync(QuestionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Возвращает вопрос по его идентификатору.
    /// </summary>
    Task<QuestionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Возвращает список вопросов, отсортированных по дате (новые первыми).
    /// </summary>
    /// <param name="limit">Количество записей (1..500).</param>
    /// <param name="offset">Смещение от начала выборки.</param>
    /// <param name="query">Опциональный поисковый запрос по вопросу или ответу.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<QuestionRecord>> ListAsync(
        int limit,
        int offset = 0,
        string? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Связывает вопрос с созданной задачей.
    /// </summary>
    Task<bool> LinkTaskAsync(Guid questionId, Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Возвращает общее количество вопросов в хранилище.
    /// </summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Заглушка хранилища вопросов для тестов и сред без базы данных.
/// </summary>
public sealed class NullQuestionStore : IQuestionStore
{
    public static NullQuestionStore Instance { get; } = new();

    public Task<Guid> AddAsync(QuestionRecord record, CancellationToken cancellationToken = default)
        => Task.FromResult(record.Id);

    public Task<QuestionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult<QuestionRecord?>(null);

    public Task<IReadOnlyList<QuestionRecord>> ListAsync(
        int limit,
        int offset = 0,
        string? query = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<QuestionRecord>>(Array.Empty<QuestionRecord>());

    public Task<bool> LinkTaskAsync(Guid questionId, Guid taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
