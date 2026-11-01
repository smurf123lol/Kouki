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
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<string> ids = await lease.Connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT t.task_id FROM task_tags t JOIN tasks k ON k.id = t.task_id " +
                "WHERE t.tag = @tag ORDER BY k.created_at DESC LIMIT @limit",
                new { tag = tag.Trim().ToLowerInvariant(), limit },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return ids.Select(Guid.Parse).ToArray();
    }
}
