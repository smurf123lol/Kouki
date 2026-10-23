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

    private readonly string _connectionString;

    /// <summary>
    /// Создаёт хранилище.
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteTaskStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = DatabaseInitializer.BuildConnectionString(DatabasePath);
    }

    /// <summary>Абсолютный путь к файлу базы.</summary>
    public string DatabasePath { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskRecord>> ListAsync(string? status, int limit, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<TaskRecord> rows = await connection.QueryAsync<TaskRecord>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM tasks " +
                "WHERE (@status IS NULL OR status = @status) " +
                "ORDER BY created_at DESC LIMIT @limit",
                new { status, limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<TaskRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<TaskRecord>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM tasks WHERE id = @id",
                new { id = id.ToString() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
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
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected = await connection.ExecuteAsync(
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
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
