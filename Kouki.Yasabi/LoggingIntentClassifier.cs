using Kouki.Kagedama.Contracts;

namespace Kouki.Yasabi;

/// <summary>
/// Декоратор, который пишет в журнал каждую классификацию — и удачную,
/// и low-confidence, и упавшую. Это и есть сбор датасета для будущего BERT.
/// </summary>
public sealed class LoggingIntentClassifier : IIntentClassifier
{
    private readonly IIntentClassifier _inner;
    private readonly IClassificationJournal _journal;
    private readonly YasabiOptions _options;
    /// <summary>
    /// Создаёт декоратор.
    /// </summary>
    /// <param name="inner">Классификатор, который реально понимает текст.</param>
    /// <param name="journal">Журнал распознавания.</param>
    /// <param name="options">Настройки.</param>
    public LoggingIntentClassifier(IIntentClassifier inner, IClassificationJournal journal, YasabiOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);

        _inner = inner;
        _journal = journal;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
    {
        string safeText = text ?? string.Empty;

        try
        {
            IntentResult result = await _inner.ClassifyAsync(safeText, cancellationToken).ConfigureAwait(false);

            Guid recordId = await RecordSafeAsync(
                new ClassificationRecord(
                    _options.Source,
                    "text",
                    safeText,
                    result.Intent,
                    result.Score,
                    result.Model,
                    result.IsConfident(_options.ConfidenceThreshold),
                    null,
                    CreatedAt: DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            // Идентификатор записи отдаём наружу: по нему хост связывает действие с датасетом.
            return recordId == Guid.Empty ? result : result with { JournalId = recordId };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _ = await RecordSafeAsync(
                new ClassificationRecord(
                    _options.Source,
                    "text",
                    safeText,
                    IntentResult.Unclassified,
                    0d,
                    _inner.GetType().Name,
                    false,
                    $"{ex.GetType().Name}: {ex.Message}",
                    CreatedAt: DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    private async Task<Guid> RecordSafeAsync(ClassificationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            return await _journal.RecordAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch when (!_options.JournalFailuresAreFatal)
        {
            // Журнал — наблюдаемость: его сбой не должен ломать обработку запроса.
            return Guid.Empty;
        }
    }
}
