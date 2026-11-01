using Dapper;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Теги задач в SQLite: отдельная таблица <c>task_tags</c>, чтобы фильтровать по тегу запросом.
/// </summary>
public sealed class SqliteTagStore : ITagStore
{
    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт хранилище с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteTagStore(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт хранилище поверх общей сессии: теги пишутся в транзакции приёма.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteTagStore(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public async Task AddAsync(
        Guid taskId,
        IEnumerable<string> tags,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);

        string[] cleaned = tags
            .Select(tag => tag?.Trim().ToLowerInvariant())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (cleaned.Length == 0)
        {
            return;
        }

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        // OR IGNORE: повторная пометка той же задачи тем же тегом — не ошибка, а no-op.
        foreach (string tag in cleaned)
        {
            await lease.Connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT OR IGNORE INTO task_tags (task_id, tag, source, created_at) " +
                    "VALUES (@taskId, @tag, @source, @createdAt)",
                    new { taskId = taskId.ToString(), tag, source, createdAt = DateTimeOffset.UtcNow },
                    transaction: lease.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<string> tags = await lease.Connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT tag FROM task_tags WHERE task_id = @taskId ORDER BY rowid",
                new { taskId = taskId.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return tags.ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> FindTasksAsync(
        string tag,
        string? status,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Статус в том же WHERE, что и тег: LIMIT должен считать уже отфильтрованные строки,
        // иначе «тег + статус + предел» отдаёт меньше задач, чем просили (или пусто).
        IEnumerable<string> ids = await lease.Connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT t.task_id FROM task_tags t JOIN tasks k ON k.id = t.task_id " +
                "WHERE t.tag = @tag AND (@status IS NULL OR k.status = @status) " +
                "ORDER BY k.created_at DESC LIMIT @limit",
                new { tag = tag.Trim().ToLowerInvariant(), status, limit },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return ids.Select(Guid.Parse).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ListForTasksAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Один запрос на весь список: дашборд обновляет его каждые пять секунд.
        IEnumerable<TagRow> rows = await lease.Connection.QueryAsync<TagRow>(
            new CommandDefinition(
                "SELECT task_id AS TaskId, tag AS Tag FROM task_tags " +
                "WHERE task_id IN @ids ORDER BY rowid",
                new { ids = ids.Select(id => id.ToString()).ToArray() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var result = new Dictionary<Guid, List<string>>();
        foreach (TagRow row in rows)
        {
            if (!Guid.TryParse(row.TaskId, out Guid taskId) || string.IsNullOrWhiteSpace(row.Tag))
            {
                continue;
            }

            if (!result.TryGetValue(taskId, out List<string>? tags))
            {
                tags = new List<string>();
                result[taskId] = tags;
            }

            tags.Add(row.Tag);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value);
    }

    /// <summary>Строка чтения тегов пачкой.</summary>
    private sealed record TagRow(string TaskId, string Tag);
}
