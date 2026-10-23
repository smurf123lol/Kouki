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
        "asr_confidence AS AsrConfidence, audio_path AS AudioPath";

    private readonly string _connectionString;

    /// <summary>
    /// Создаёт журнал.
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteClassificationJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = DatabaseInitializer.BuildConnectionString(databasePath);
    }

    /// <inheritdoc />
    public async Task<Guid> RecordAsync(ClassificationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var id = Guid.NewGuid();
        DateTimeOffset createdAt = record.CreatedAt ?? DateTimeOffset.UtcNow;

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

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
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClassificationEntry>> ListAsync(int limit, bool onlyUnresolved, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ClassificationEntry> rows = await connection.QueryAsync<ClassificationEntry>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM classifications " +
                (onlyUnresolved ? "WHERE label IS NULL AND (confident = 0 OR error IS NOT NULL) " : string.Empty) +
                "ORDER BY created_at DESC LIMIT @limit",
                new { limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<bool> AnnotateAudioAsync(Guid id, double? asrConfidence, string? audioPath, CancellationToken cancellationToken = default, string? source = null)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE classifications SET asr_confidence = @asrConfidence, audio_path = @audioPath, source = COALESCE(@source, source) WHERE id = @id",
                new { id = id.ToString(), asrConfidence, audioPath, source },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClassificationEntry>> ListLabeledAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ClassificationEntry> rows = await connection.QueryAsync<ClassificationEntry>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM classifications WHERE label IS NOT NULL " +
                "ORDER BY labeled_at DESC LIMIT @limit",
                new { limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<bool> LabelAsync(Guid id, string label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE classifications SET label = @label, labeled_at = @labeledAt WHERE id = @id",
                new { id = id.ToString(), label, labeledAt = DateTimeOffset.UtcNow.ToString("O") },
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
