using System.Globalization;

using Dapper;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Хранилище напоминаний в SQLite.
/// </summary>
/// <remarks>
/// Время лежит текстом в UTC и одинаковой длины (<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>),
/// поэтому сравнение «срок настал» — обычное строковое: оно не зависит от часового пояса
/// сервера и от того, кто записал строку.
/// </remarks>
public sealed class SqliteReminderStore : IReminderStore
{
    private const string SelectColumns =
        "id AS Id, created_at AS CreatedAt, due_at AS DueAt, text AS Text, repeat_rule AS RepeatRule, " +
        "state AS State, source AS Source, task_id AS TaskId, sent_at AS SentAt, acknowledged_at AS AcknowledgedAt";

    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт хранилище с собственной сессией (тесты, утилиты).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteReminderStore(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт хранилище поверх общей сессии.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteReminderStore(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public async Task<ReminderRecord> AddAsync(
        string text,
        DateTimeOffset dueAt,
        string? repeat,
        string source,
        Guid? taskId,
        CancellationToken cancellationToken = default)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Текст напоминания пуст.", nameof(text));
        }

        ReminderRecord record = new(
            Guid.NewGuid(),
            trimmed,
            dueAt.ToUniversalTime(),
            NormalizeRepeat(repeat),
            ReminderStates.Pending,
            string.IsNullOrWhiteSpace(source) ? ReminderSources.Api : source.Trim(),
            taskId,
            DateTimeOffset.UtcNow,
            null,
            null);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO reminders (id, created_at, due_at, text, repeat_rule, state, source, task_id, sent_at, acknowledged_at) " +
                "VALUES (@id, @createdAt, @dueAt, @text, @repeat, @state, @source, @taskId, NULL, NULL)",
                new
                {
                    id = record.Id.ToString(),
                    createdAt = Format(record.CreatedAt),
                    dueAt = Format(record.DueAt),
                    text = record.Text,
                    repeat = record.Repeat,
                    state = record.State,
                    source = record.Source,
                    taskId = record.TaskId?.ToString(),
                },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return record;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReminderRecord>> ListAsync(
        bool openOnly,
        int limit,
        CancellationToken cancellationToken = default)
    {
        int bounded = Math.Clamp(limit, 1, 500);
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ReminderRow> rows = await lease.Connection.QueryAsync<ReminderRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM reminders " +
                "WHERE (@openOnly = 0 OR state IN ('pending', 'sent')) " +
                "ORDER BY due_at ASC LIMIT @limit",
                new { openOnly = openOnly ? 1 : 0, limit = bounded },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReminderRecord>> DueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        int bounded = Math.Clamp(limit, 1, 200);
        string moment = Format(now);
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ReminderRow> rows = await lease.Connection.QueryAsync<ReminderRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM reminders " +
                "WHERE state = 'pending' AND due_at <= @now " +
                "ORDER BY due_at ASC LIMIT @limit",
                new { now = moment, limit = bounded },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        List<ReminderRecord> due = rows.Select(Map).ToList();
        if (due.Count == 0)
        {
            return due;
        }

        // Помечаем отправленными в той же транзакции: иначе бот и станция,
        // опросив одновременно, отправят одно напоминание дважды.
        foreach (ReminderRecord item in due)
        {
            await lease.Connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE reminders SET state = 'sent', sent_at = COALESCE(sent_at, @sentAt) WHERE id = @id",
                    new { id = item.Id.ToString(), sentAt = moment },
                    transaction: lease.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return due
            .Select(item => item with { State = ReminderStates.Sent, SentAt = now.ToUniversalTime() })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReminderRecord?> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await UpdateStateAsync(
            id,
            "UPDATE reminders SET state = 'cancelled' WHERE id = @id AND state IN ('pending', 'sent')",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ReminderRecord?> AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ReminderRecord? updated = await UpdateStateAsync(
            id,
            "UPDATE reminders SET state = 'acknowledged', acknowledged_at = @now " +
            "WHERE id = @id AND state IN ('pending', 'sent')",
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
        {
            return null;
        }

        // Повторяющееся напоминание живо и после подтверждения: ставим следующий срок.
        if (string.Equals(updated.Repeat, "daily", StringComparison.Ordinal))
        {
            return await RecurAsync(updated, TimeSpan.FromDays(1), cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(updated.Repeat, "weekly", StringComparison.Ordinal))
        {
            return await RecurAsync(updated, TimeSpan.FromDays(7), cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task<ReminderRecord?> SnoozeAsync(
        Guid id,
        TimeSpan delay,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Отложить можно только в будущее.");
        }

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE reminders SET state = 'pending', due_at = @dueAt, sent_at = NULL " +
                "WHERE id = @id AND state IN ('pending', 'sent')",
                new
                {
                    id = id.ToString(),
                    dueAt = Format(now.ToUniversalTime().Add(delay)),
                },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return await GetAsync(lease, id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LinkTaskAsync(Guid reminderId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE reminders SET task_id = @taskId WHERE id = @id",
                new { id = reminderId.ToString(), taskId = taskId.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReminderRecord>> ForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ReminderRow> rows = await lease.Connection.QueryAsync<ReminderRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM reminders WHERE task_id = @taskId ORDER BY due_at ASC",
                new { taskId = taskId.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(Map).ToList();
    }

    private async Task<ReminderRecord?> UpdateStateAsync(
        Guid id,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        int changed = await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { id = id.ToString(), now = Format(DateTimeOffset.UtcNow) },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return changed == 0 ? null : await GetAsync(lease, id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReminderRecord?> RecurAsync(
        ReminderRecord source,
        TimeSpan step,
        CancellationToken cancellationToken)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset next = source.DueAt;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        while (next <= now)
        {
            next = next.Add(step);
        }

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE reminders SET state = 'pending', due_at = @dueAt, sent_at = NULL, acknowledged_at = NULL WHERE id = @id",
                new { id = source.Id.ToString(), dueAt = Format(next) },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return await GetAsync(lease, source.Id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ReminderRecord?> GetAsync(
        SqliteLease lease,
        Guid id,
        CancellationToken cancellationToken)
    {
        ReminderRow? row = await lease.Connection.QuerySingleOrDefaultAsync<ReminderRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM reminders WHERE id = @id",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    private static ReminderRecord Map(ReminderRow row) => new(
        Guid.Parse(row.Id),
        row.Text,
        Parse(row.DueAt),
        row.RepeatRule,
        row.State,
        row.Source,
        string.IsNullOrWhiteSpace(row.TaskId) ? null : Guid.Parse(row.TaskId),
        Parse(row.CreatedAt),
        string.IsNullOrWhiteSpace(row.SentAt) ? null : Parse(row.SentAt),
        string.IsNullOrWhiteSpace(row.AcknowledgedAt) ? null : Parse(row.AcknowledgedAt));

    private static string? NormalizeRepeat(string? repeat)
    {
        if (string.IsNullOrWhiteSpace(repeat))
        {
            return null;
        }

        string value = repeat.Trim().ToLowerInvariant();
        return value is "daily" or "weekly" ? value : null;
    }

    /// <summary>Формат времени в базе: UTC, фиксированная длина, сортировка строкой.</summary>
    /// <param name="value">Время.</param>
    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed record ReminderRow(
        string Id,
        string CreatedAt,
        string DueAt,
        string Text,
        string? RepeatRule,
        string State,
        string Source,
        string? TaskId,
        string? SentAt,
        string? AcknowledgedAt);
}

/// <summary>
/// Допустимые источники напоминаний (те же, что у приёма, плюс «command»).
/// </summary>
public static class ReminderSources
{
    /// <summary>HTTP API.</summary>
    public const string Api = "api";

    /// <summary>Telegram-бот.</summary>
    public const string Telegram = "telegram";

    /// <summary>Голосовая станция.</summary>
    public const string Station = "station";

    /// <summary>Дашборд.</summary>
    public const string Dashboard = "dashboard";
}
