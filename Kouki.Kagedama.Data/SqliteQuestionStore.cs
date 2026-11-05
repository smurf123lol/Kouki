using Dapper;
using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Хранилище истории вопросов и ответов модели в SQLite.
/// </summary>
public sealed class SqliteQuestionStore : IQuestionStore
{
    private const string SelectColumns = """
        id AS Id,
        created_at AS CreatedAt,
        source AS Source,
        question AS Question,
        answer AS Answer,
        task_id AS TaskId,
        model AS Model,
        duration_ms AS DurationMs,
        audio_path AS AudioPath
        """;

    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт хранилище с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteQuestionStore(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт хранилище поверх общей сессии базы данных.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteQuestionStore(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public async Task<Guid> AddAsync(QuestionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO questions (
                    id, created_at, source, question, answer, task_id, model, duration_ms, audio_path)
                VALUES (
                    @id, @createdAt, @source, @question, @answer, @taskId, @model, @durationMs, @audioPath);
                """,
                new DynamicParameters(new Dictionary<string, object?>
                {
                    ["id"] = record.Id.ToString(),
                    ["createdAt"] = record.CreatedAt,
                    ["source"] = record.Source,
                    ["question"] = record.Question,
                    ["answer"] = record.Answer,
                    ["taskId"] = record.TaskId?.ToString(),
                    ["model"] = record.Model,
                    ["durationMs"] = record.DurationMs,
                    ["audioPath"] = record.AudioPath,
                }),
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return record.Id;
    }

    /// <inheritdoc />
    public async Task<QuestionRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await lease.Connection.QuerySingleOrDefaultAsync<QuestionRecord>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                FROM questions
                WHERE id = @id;
                """,
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuestionRecord>> ListAsync(
        int limit,
        int offset = 0,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(limit, 1, 500);
        int skip = Math.Max(0, offset);
        string? queryPattern = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<QuestionRecord> rows = await lease.Connection.QueryAsync<QuestionRecord>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                FROM questions
                WHERE (@queryPattern IS NULL OR (question LIKE @queryPattern OR answer LIKE @queryPattern))
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset;
                """,
                new { queryPattern, limit = take, offset = skip },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<bool> LinkTaskAsync(Guid questionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected = await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE questions
                SET task_id = @taskId
                WHERE id = @id;
                """,
                new { id = questionId.ToString(), taskId = taskId.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await lease.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM questions;",
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
