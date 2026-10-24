using Dapper;
using Microsoft.Data.Sqlite;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Хранилище задач в SQLite (файл <c>kouki.sqlite</c>).
/// </summary>
public sealed class SqliteTaskStore : ITaskStore
{
    private const string SelectColumns =
        "id AS Id, created_at AS CreatedAt, updated_at AS UpdatedAt, source AS Source, " +
        "raw_text AS RawText, title AS Title, status AS Status, priority AS Priority, " +
        "workspace AS Workspace, meta_json AS MetaJson";

    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт хранилище с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteTaskStore(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт хранилище поверх общей сессии: тогда транзакция приёма охватывает все хранилища.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteTaskStore(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Абсолютный путь к файлу базы.</summary>
    public string DatabasePath => _session.DatabasePath;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskRecord>> ListAsync(string? status, int limit, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<TaskRecord> rows = await lease.Connection.QueryAsync<TaskRecord>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM tasks " +
                "WHERE (@status IS NULL OR status = @status) " +
                "ORDER BY created_at DESC LIMIT @limit",
                new { status, limit },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<TaskRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await lease.Connection.QuerySingleOrDefaultAsync<TaskRecord>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM tasks WHERE id = @id",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO tasks (id, created_at, updated_at, source, raw_text, title, status, priority, workspace, meta_json) " +
                "VALUES (@id, @createdAt, @updatedAt, @source, @rawText, @title, @status, @priority, @workspace, @metaJson)",
                new DynamicParameters(new Dictionary<string, object?>
                {
                    ["id"] = record.Id.ToString(),
                    ["createdAt"] = record.CreatedAt,
                    ["updatedAt"] = record.UpdatedAt,
                    ["source"] = record.Source,
                    ["rawText"] = record.RawText,
                    ["title"] = record.Title,
                    ["status"] = record.Status,
                    ["priority"] = record.Priority,
                    ["workspace"] = record.Workspace,
                    ["metaJson"] = record.MetaJson,
                }),
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid id,
        string? status,
        string? title,
        int? priority,
        string? metaJson = null,
        CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected = await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE tasks SET " +
                "status = COALESCE(@status, status), " +
                "title = COALESCE(@title, title), " +
                "priority = COALESCE(@priority, priority), " +
                "meta_json = COALESCE(@metaJson, meta_json), " +
                "updated_at = @updatedAt " +
                "WHERE id = @id",
                new
                {
                    id = id.ToString(),
                    status,
                    title,
                    priority,
                    metaJson,
                    updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }
}
