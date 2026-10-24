using Dapper;
using Kouki.Kagedama.Contracts;
using Microsoft.Data.Sqlite;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Журнал распознавания в SQLite: каждая классификация, включая неуверенные и упавшие.
/// Эти записи — заготовка датасета для будущего BERT.
/// </summary>
public sealed class SqliteClassificationJournal : IClassificationJournal
{
    private const string SelectColumns =
        "id AS Id, created_at AS CreatedAt, source AS Source, input_kind AS InputKind, " +
        "input_text AS InputText, intent AS Intent, score AS Score, confident AS Confident, " +
        "model AS Model, error AS Error, label AS Label, labeled_at AS LabeledAt, " +
        "asr_confidence AS AsrConfidence, audio_path AS AudioPath, hinted_intent AS HintedIntent, task_id AS TaskId";

    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт журнал с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteClassificationJournal(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт журнал поверх общей сессии: тогда запись журнала попадает в ту же транзакцию,
    /// что и задача.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteClassificationJournal(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public async Task<Guid> RecordAsync(ClassificationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var id = Guid.NewGuid();
        DateTimeOffset createdAt = record.CreatedAt ?? DateTimeOffset.UtcNow;

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO classifications " +
                "(id, task_id, created_at, source, input_kind, input_text, asr_confidence, intent, score, confident, model, error, audio_path) " +
                "VALUES (@id, NULL, @createdAt, @source, @inputKind, @inputText, @asrConfidence, @intent, @score, @confident, @model, @error, @audioPath)",
                new
                {
                    id = id.ToString(),
                    createdAt = createdAt.ToString("O"),
                    source = record.Source,
                    inputKind = record.InputKind,
                    inputText = record.InputText,
                    asrConfidence = record.AsrConfidence,
                    intent = record.Intent,
                    score = record.Score,
                    confident = record.Confident,
                    model = record.Model,
                    error = record.Error,
                    audioPath = record.AudioPath,
                },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClassificationEntry>> ListAsync(int limit, bool onlyUnresolved, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        IEnumerable<ClassificationEntry> rows = await connection.QueryAsync<ClassificationEntry>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM classifications " +
                (onlyUnresolved ? "WHERE label IS NULL AND hinted_intent IS NULL AND (confident = 0 OR error IS NOT NULL) " : string.Empty) +
                "ORDER BY created_at DESC LIMIT @limit",
                new { limit },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<bool> AnnotateAudioAsync(Guid id, double? asrConfidence, string? audioPath, CancellationToken cancellationToken = default, string? source = null)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        int affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE classifications SET asr_confidence = @asrConfidence, audio_path = @audioPath, source = COALESCE(@source, source) WHERE id = @id",
                new { id = id.ToString(), asrConfidence, audioPath, source },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> AnnotateHintAsync(Guid id, string? hintedIntent, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        // Подсказка сессии пишется отдельно: вердикт правил и его score остаются в записи,
        // иначе по журналу нельзя понять, насколько хорошо работает классификатор.
        int affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE classifications SET hinted_intent = @hintedIntent WHERE id = @id",
                new { id = id.ToString(), hintedIntent },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClassificationEntry>> ListLabeledAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        IEnumerable<ClassificationEntry> rows = await connection.QueryAsync<ClassificationEntry>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM classifications WHERE label IS NOT NULL " +
                "ORDER BY labeled_at DESC LIMIT @limit",
                new { limit },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<bool> LabelAsync(Guid id, string label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        int affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE classifications SET label = @label, labeled_at = @labeledAt WHERE id = @id",
                new { id = id.ToString(), label, labeledAt = DateTimeOffset.UtcNow.ToString("O") },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> LinkToTaskAsync(Guid id, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        // Ссылка «фраза → задача»: без неё нельзя ни связать запись журнала с задачей
        // в дашборде, ни собрать датасет по задачам.
        int affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE classifications SET task_id = @taskId WHERE id = @id",
                new { id = id.ToString(), taskId = taskId.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }
}
