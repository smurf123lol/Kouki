using Dapper;
using Microsoft.Data.Sqlite;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;

namespace Kouki.Tests;

/// <summary>
/// Тесты журнала запросов и телеметрии (T20): запись метрик, кольцевой буфер и агрегаты.
/// </summary>
public sealed class RequestLogTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly SqliteRequestLog _requestLog;
    private readonly SqliteActionLog _actionLog;

    public RequestLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-request-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _databasePath = Path.Combine(_directory, "kouki.sqlite");
        DatabaseInitializer.Initialize(_databasePath);
        _requestLog = new SqliteRequestLog(_databasePath);
        _actionLog = new SqliteActionLog(_databasePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Игнорируем задержки освобождения файла SQLite в тестах
        }
    }

    [Fact]
    public async Task RecordAsync_writes_request_entry_with_all_metrics()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entry = new RequestLogEntry(
            Id: id,
            CreatedAt: now,
            Endpoint: RequestEndpoints.Voice,
            Source: "station",
            HttpStatus: 200,
            Outcome: RequestOutcomes.Ok,
            DurationMs: 450,
            Intent: "add_task",
            TaskId: null,
            ClassificationId: null,
            AudioBytes: 64000,
            AudioDurationSec: 2.0,
            Error: null);

        Guid recordedId = await _requestLog.RecordAsync(entry);
        Assert.Equal(id, recordedId);

        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        var row = await connection.QuerySingleAsync(
            """
            SELECT kind, command, source, outcome, duration_ms, http_status, audio_bytes, audio_duration_sec, args_json, error
            FROM actions
            WHERE id = @id;
            """,
            new { id = id.ToString() });

        Assert.Equal("request", (string)row.kind);
        Assert.Equal("voice", (string)row.command);
        Assert.Equal("station", (string)row.source);
        Assert.Equal("ok", (string)row.outcome);
        Assert.Equal(450L, (long)row.duration_ms);
        Assert.Equal(200L, (long)row.http_status);
        Assert.Equal(64000L, (long)row.audio_bytes);
        Assert.Equal(2.0, (double)row.audio_duration_sec);
        Assert.Contains("\"httpStatus\":200", (string)row.args_json);
        Assert.Null(row.error);
    }

    [Fact]
    public async Task RecordAsync_writes_rejected_request_with_error_text()
    {
        var id = Guid.NewGuid();
        var entry = new RequestLogEntry(
            Id: id,
            CreatedAt: DateTimeOffset.UtcNow,
            Endpoint: RequestEndpoints.Voice,
            Source: "station",
            HttpStatus: 400,
            Outcome: RequestOutcomes.Rejected,
            DurationMs: 12,
            Error: "Пустое тело запроса: ожидался WAV.");

        await _requestLog.RecordAsync(entry);

        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        var row = await connection.QuerySingleAsync(
            "SELECT outcome, http_status, error FROM actions WHERE id = @id;",
            new { id = id.ToString() });

        Assert.Equal("rejected", (string)row.outcome);
        Assert.Equal(400L, (long)row.http_status);
        Assert.Equal("Пустое тело запроса: ожидался WAV.", (string)row.error);
    }

    [Fact]
    public async Task RecordAsync_prunes_older_records_when_exceeding_max_rows()
    {
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ids = new List<Guid>();

        for (int i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            var entry = new RequestLogEntry(
                Id: id,
                CreatedAt: baseTime.AddMinutes(i),
                Endpoint: RequestEndpoints.Classify,
                Source: "dashboard",
                HttpStatus: 200,
                Outcome: RequestOutcomes.Ok,
                DurationMs: 50 + i);

            await _requestLog.RecordAsync(entry, maxRows: 3);
        }

        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        var remainingIds = (await connection.QueryAsync<string>(
            "SELECT id FROM actions WHERE kind = 'request' ORDER BY created_at ASC;")).ToList();

        Assert.Equal(3, remainingIds.Count);
        // Первые две записи (0 и 1) должны быть удалены, остались 2, 3, 4
        Assert.DoesNotContain(ids[0].ToString(), remainingIds);
        Assert.DoesNotContain(ids[1].ToString(), remainingIds);
        Assert.Contains(ids[2].ToString(), remainingIds);
        Assert.Contains(ids[3].ToString(), remainingIds);
        Assert.Contains(ids[4].ToString(), remainingIds);
    }

    [Fact]
    public async Task RecordAsync_does_not_prune_other_action_kinds()
    {
        var commandActionId = Guid.NewGuid();
        await _actionLog.RecordAsync(new ActionRecord(
            Id: commandActionId,
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
            TaskId: null,
            ClassificationId: null,
            Source: "mcp",
            Kind: ActionKinds.Command,
            Intent: null,
            Command: "list_tasks",
            Outcome: ActionOutcomes.Executed));

        for (int i = 0; i < 5; i++)
        {
            await _requestLog.RecordAsync(new RequestLogEntry(
                Id: Guid.NewGuid(),
                CreatedAt: DateTimeOffset.UtcNow.AddMinutes(i),
                Endpoint: RequestEndpoints.Classify,
                Source: "dashboard",
                HttpStatus: 200,
                Outcome: RequestOutcomes.Ok,
                DurationMs: 10), maxRows: 2);
        }

        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        int commandCount = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM actions WHERE id = @id;",
            new { id = commandActionId.ToString() });
        int requestCount = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM actions WHERE kind = 'request';");

        Assert.Equal(1, commandCount);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task RecordAsync_with_zero_max_rows_does_not_prune()
    {
        for (int i = 0; i < 5; i++)
        {
            await _requestLog.RecordAsync(new RequestLogEntry(
                Id: Guid.NewGuid(),
                CreatedAt: DateTimeOffset.UtcNow.AddMinutes(i),
                Endpoint: RequestEndpoints.Classify,
                Source: "dashboard",
                HttpStatus: 200,
                Outcome: RequestOutcomes.Ok,
                DurationMs: 10), maxRows: 0);
        }

        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        int count = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM actions WHERE kind = 'request';");

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task SummarizeAsync_calculates_totals_and_breakdowns_accurately()
    {
        var now = DateTimeOffset.UtcNow;

        // 1. voice: station, ok, 1000 ms, 32000 bytes
        await _requestLog.RecordAsync(new RequestLogEntry(
            Guid.NewGuid(), now.AddMinutes(-5), RequestEndpoints.Voice, "station", 200, RequestOutcomes.Ok, 1000,
            AudioBytes: 32000, AudioDurationSec: 1.0), maxRows: 0);

        // 2. voice: station, rejected (400), 200 ms, 0 bytes
        await _requestLog.RecordAsync(new RequestLogEntry(
            Guid.NewGuid(), now.AddMinutes(-4), RequestEndpoints.Voice, "station", 400, RequestOutcomes.Rejected, 200,
            AudioBytes: 0, AudioDurationSec: 0, Error: "error"), maxRows: 0);

        // 3. classify: dashboard, ok, 300 ms, null bytes
        await _requestLog.RecordAsync(new RequestLogEntry(
            Guid.NewGuid(), now.AddMinutes(-3), RequestEndpoints.Classify, "dashboard", 200, RequestOutcomes.Ok, 300), maxRows: 0);

        // 4. classify: api, error (500), 500 ms, null bytes
        await _requestLog.RecordAsync(new RequestLogEntry(
            Guid.NewGuid(), now.AddMinutes(-2), RequestEndpoints.Classify, "api", 500, RequestOutcomes.Error, 500), maxRows: 0);

        // 5. Старая запись вне окна (25 ч назад)
        await _requestLog.RecordAsync(new RequestLogEntry(
            Guid.NewGuid(), now.AddHours(-25), RequestEndpoints.Voice, "station", 200, RequestOutcomes.Ok, 9999,
            AudioBytes: 100000), maxRows: 0);

        RequestLogSummary summary = await _requestLog.SummarizeAsync(hours: 24);

        Assert.Equal(24, summary.WindowHours);
        Assert.Equal(4, summary.Totals.Requests);
        Assert.Equal(2, summary.Totals.Errors);
        // (1000 + 200 + 300 + 500) / 4 = 500.0
        Assert.Equal(500.0, summary.Totals.AvgDurationMs);
        Assert.Equal(32000L, summary.Totals.AudioBytes);

        // byEndpoint
        Assert.Equal(2, summary.ByEndpoint.Count);
        var voiceEp = summary.ByEndpoint.First(e => e.Endpoint == "voice");
        Assert.Equal(2, voiceEp.Requests);
        Assert.Equal(1, voiceEp.Errors);
        Assert.Equal(600.0, voiceEp.AvgDurationMs);
        Assert.Equal(32000L, voiceEp.AudioBytes);

        var classifyEp = summary.ByEndpoint.First(e => e.Endpoint == "classify");
        Assert.Equal(2, classifyEp.Requests);
        Assert.Equal(1, classifyEp.Errors);
        Assert.Equal(400.0, classifyEp.AvgDurationMs);
        Assert.Equal(0L, classifyEp.AudioBytes);

        // bySource
        Assert.Equal(3, summary.BySource.Count);
        var stationSource = summary.BySource.First(s => s.Source == "station");
        Assert.Equal(2, stationSource.Requests);
        Assert.Equal(1, stationSource.Errors);

        var dashboardSource = summary.BySource.First(s => s.Source == "dashboard");
        Assert.Equal(1, dashboardSource.Requests);
        Assert.Equal(0, dashboardSource.Errors);
    }

    [Fact]
    public async Task SummarizeAsync_empty_returns_zeroes_and_empty_lists()
    {
        RequestLogSummary summary = await _requestLog.SummarizeAsync(hours: 24);

        Assert.Equal(24, summary.WindowHours);
        Assert.Equal(0, summary.Totals.Requests);
        Assert.Equal(0, summary.Totals.Errors);
        Assert.Equal(0.0, summary.Totals.AvgDurationMs);
        Assert.Equal(0L, summary.Totals.AudioBytes);
        Assert.Empty(summary.ByEndpoint);
        Assert.Empty(summary.BySource);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(169)]
    [InlineData(500)]
    public async Task SummarizeAsync_invalid_hours_throws_argument_out_of_range(int hours)
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _requestLog.SummarizeAsync(hours));
        Assert.Contains("hours: целое число от 1 до 168.", ex.Message);
    }

    [Fact]
    public void DatabaseInitializer_is_idempotent_and_creates_indexes()
    {
        // Повторный запуск на существующей базе не должен падать
        DatabaseInitializer.Initialize(_databasePath);

        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_actions_kind_created';";
        long indexCount = (long)command.ExecuteScalar()!;
        Assert.Equal(1L, indexCount);
    }
}
