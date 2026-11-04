using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Хранилище журнала запросов к API и сервис телеметрии на базе SQLite.
/// Записывает обращения в таблицу actions (kind = 'request') и считает сводные метрики.
/// </summary>
public sealed class SqliteRequestLog : IRequestLog
{
    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт журнал запросов с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы данных.</param>
    public SqliteRequestLog(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт журнал запросов поверх общей сессии.
    /// </summary>
    /// <param name="session">Сессия базы данных.</param>
    public SqliteRequestLog(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public async Task<Guid> RecordAsync(
        RequestLogEntry entry,
        int maxRows = 10000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string argsJson = JsonSerializer.Serialize(new
        {
            httpStatus = entry.HttpStatus,
            audioBytes = entry.AudioBytes,
            audioDurationSec = entry.AudioDurationSec,
        });

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO actions (
                    id, created_at, task_id, classification_id, source, kind,
                    intent, command, outcome, args_json, result_json, error,
                    duration_ms, http_status, audio_bytes, audio_duration_sec)
                VALUES (
                    @id, @createdAt, @taskId, @classificationId, @source, @kind,
                    @intent, @command, @outcome, @argsJson, NULL, @error,
                    @durationMs, @httpStatus, @audioBytes, @audioDurationSec);
                """,
                new
                {
                    id = entry.Id.ToString(),
                    createdAt = entry.CreatedAt.ToString("O"),
                    taskId = entry.TaskId?.ToString(),
                    classificationId = entry.ClassificationId?.ToString(),
                    source = entry.Source,
                    kind = ActionKinds.Request,
                    intent = entry.Intent,
                    command = entry.Endpoint,
                    outcome = entry.Outcome,
                    argsJson,
                    error = entry.Error,
                    durationMs = entry.DurationMs,
                    httpStatus = entry.HttpStatus,
                    audioBytes = entry.AudioBytes,
                    audioDurationSec = entry.AudioDurationSec,
                },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (maxRows > 0)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    DELETE FROM actions
                    WHERE kind = 'request' AND id IN (
                        SELECT id FROM actions
                        WHERE kind = 'request'
                        ORDER BY created_at DESC
                        LIMIT -1 OFFSET @maxRows
                    );
                    """,
                    new { maxRows },
                    transaction: lease.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return entry.Id;
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(int maxRows, CancellationToken cancellationToken = default)
    {
        if (maxRows <= 0)
        {
            return 0;
        }

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        return await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM actions
                WHERE kind = 'request' AND id IN (
                    SELECT id FROM actions
                    WHERE kind = 'request'
                    ORDER BY created_at DESC
                    LIMIT -1 OFFSET @maxRows
                );
                """,
                new { maxRows },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RequestLogSummary> SummarizeAsync(int hours, CancellationToken cancellationToken = default)
    {
        if (hours < 1 || hours > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), "hours: целое число от 1 до 168.");
        }

        DateTimeOffset since = DateTimeOffset.UtcNow.AddHours(-hours);
        string sinceStr = since.ToString("O");

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        TotalsRow totalsRow = await connection.QuerySingleAsync<TotalsRow>(
            new CommandDefinition(
                """
                SELECT
                    COUNT(*) AS Requests,
                    COALESCE(SUM(CASE WHEN outcome <> 'ok' THEN 1 ELSE 0 END), 0) AS Errors,
                    COALESCE(AVG(duration_ms), 0.0) AS AvgDurationMs,
                    COALESCE(SUM(COALESCE(audio_bytes, 0)), 0) AS AudioBytes
                FROM actions
                WHERE kind = 'request' AND created_at >= @sinceStr;
                """,
                new { sinceStr },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        IEnumerable<EndpointRow> endpointRows = await connection.QueryAsync<EndpointRow>(
            new CommandDefinition(
                """
                SELECT
                    command AS Endpoint,
                    COUNT(*) AS Requests,
                    COALESCE(SUM(CASE WHEN outcome <> 'ok' THEN 1 ELSE 0 END), 0) AS Errors,
                    COALESCE(AVG(duration_ms), 0.0) AS AvgDurationMs,
                    COALESCE(SUM(COALESCE(audio_bytes, 0)), 0) AS AudioBytes
                FROM actions
                WHERE kind = 'request' AND created_at >= @sinceStr
                GROUP BY command
                ORDER BY Requests DESC;
                """,
                new { sinceStr },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        IEnumerable<SourceRow> sourceRows = await connection.QueryAsync<SourceRow>(
            new CommandDefinition(
                """
                SELECT
                    source AS Source,
                    COUNT(*) AS Requests,
                    COALESCE(SUM(CASE WHEN outcome <> 'ok' THEN 1 ELSE 0 END), 0) AS Errors
                FROM actions
                WHERE kind = 'request' AND created_at >= @sinceStr
                GROUP BY source
                ORDER BY Requests DESC;
                """,
                new { sinceStr },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var totals = new RequestLogTotals(
            totalsRow.Requests,
            totalsRow.Errors,
            Math.Round(totalsRow.AvgDurationMs, 1),
            totalsRow.AudioBytes);

        var byEndpoint = endpointRows
            .Select(r => new RequestLogEndpointSummary(
                r.Endpoint ?? string.Empty,
                r.Requests,
                r.Errors,
                Math.Round(r.AvgDurationMs, 1),
                r.AudioBytes))
            .ToList();

        var bySource = sourceRows
            .Select(r => new RequestLogSourceSummary(
                r.Source ?? string.Empty,
                r.Requests,
                r.Errors))
            .ToList();

        return new RequestLogSummary(hours, since, totals, byEndpoint, bySource);
    }

    private sealed class TotalsRow
    {
        public int Requests { get; set; }
        public int Errors { get; set; }
        public double AvgDurationMs { get; set; }
        public long AudioBytes { get; set; }
    }

    private sealed class EndpointRow
    {
        public string? Endpoint { get; set; }
        public int Requests { get; set; }
        public int Errors { get; set; }
        public double AvgDurationMs { get; set; }
        public long AudioBytes { get; set; }
    }

    private sealed class SourceRow
    {
        public string? Source { get; set; }
        public int Requests { get; set; }
        public int Errors { get; set; }
    }
}
