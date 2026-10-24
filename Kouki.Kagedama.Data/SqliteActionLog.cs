using Dapper;
using Microsoft.Data.Sqlite;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Журнал действий в SQLite. Пишется после каждого приёма фразы: что распознали,
/// что исполнили, что вернулось и сколько это заняло.
/// </summary>
public sealed class SqliteActionLog : IActionLog
{
    private const string SelectColumns = """
        id AS Id,
        created_at AS CreatedAt,
        task_id AS TaskId,
        classification_id AS ClassificationId,
        source AS Source,
        kind AS Kind,
        intent AS Intent,
        command AS Command,
        outcome AS Outcome,
        args_json AS ArgsJson,
        result_json AS ResultJson,
        error AS Error,
        duration_ms AS DurationMs
        """;

    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт журнал действий с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteActionLog(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт журнал действий поверх общей сессии.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteActionLog(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public async Task<Guid> RecordAsync(ActionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO actions (
                    id, created_at, task_id, classification_id, source, kind,
                    intent, command, outcome, args_json, result_json, error, duration_ms)
                VALUES (
                    @id, @createdAt, @taskId, @classificationId, @source, @kind,
                    @intent, @command, @outcome, @argsJson, @resultJson, @error, @durationMs);
                """,
                new DynamicParameters(new Dictionary<string, object?>
                {
                    ["id"] = record.Id.ToString(),
                    ["createdAt"] = record.CreatedAt,
                    ["taskId"] = record.TaskId?.ToString(),
                    ["classificationId"] = record.ClassificationId?.ToString(),
                    ["source"] = record.Source,
                    ["kind"] = record.Kind,
                    ["intent"] = record.Intent,
                    ["command"] = record.Command,
                    ["outcome"] = record.Outcome,
                    ["argsJson"] = record.ArgsJson,
                    ["resultJson"] = record.ResultJson,
                    ["error"] = record.Error,
                    ["durationMs"] = record.DurationMs,
                }),
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return record.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActionRecord>> ListAsync(int limit, Guid? taskId = null, CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(limit, 1, 500);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ActionRecord> rows = await lease.Connection.QueryAsync<ActionRecord>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                FROM actions
                WHERE (@taskId IS NULL OR task_id = @taskId)
                ORDER BY created_at DESC
                LIMIT @limit;
                """,
                new { taskId = taskId?.ToString(), limit = take },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }
}
