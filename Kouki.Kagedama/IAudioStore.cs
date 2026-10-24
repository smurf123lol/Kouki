using Microsoft.Extensions.Logging;

namespace Kouki.Kagedama;

/// <summary>
/// Хранилище голосовых записей.
/// </summary>
/// <remarks>
/// Записи нужны как материал для будущего обучения: пока разметки нет — аудио лежит,
/// после разметки <see cref="RemoveLabeledAsync"/> может его удалить. Отдельно работает
/// срок хранения (<see cref="RemoveOlderThanAsync"/>): это настройка, а не константа.
/// </remarks>
public interface IAudioStore
{
    /// <summary>
    /// Сохраняет запись и возвращает путь, который попадает в журнал распознавания.
    /// </summary>
    /// <param name="wav">Байты WAV.</param>
    /// <param name="source">Источник записи: station, dashboard, telegram.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<string> SaveAsync(byte[] wav, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет записи, чьи распознавания уже размечены.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сколько файлов удалено.</returns>
    Task<int> RemoveLabeledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет записи старше <paramref name="cutoff"/>.
    /// </summary>
    /// <param name="cutoff">Всё, что записано раньше этого момента, удаляется.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сколько файлов удалено.</returns>
    Task<int> RemoveOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

/// <summary>
/// Хранилище аудио в файловой системе: <c>{корень}/{источник}/{yyyyMMdd}/{время}_{guid}.wav</c>.
/// </summary>
public sealed class FileAudioStore : IAudioStore
{
    private readonly string _root;
    private readonly Contracts.IClassificationJournal _journal;
    private readonly Microsoft.Extensions.Logging.ILogger<FileAudioStore> _logger;

    /// <summary>
    /// Создаёт хранилище.
    /// </summary>
    /// <param name="rootDirectory">Каталог для записей.</param>
    /// <param name="journal">Журнал распознавания: по нему видно, что уже размечено.</param>
    /// <param name="logger">Лог.</param>
    public FileAudioStore(
        string rootDirectory,
        Contracts.IClassificationJournal journal,
        Microsoft.Extensions.Logging.ILogger<FileAudioStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(logger);

        _root = Path.GetFullPath(rootDirectory);
        _journal = journal;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(byte[] wav, string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wav);

        string safeSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        string directory = Path.Combine(_root, Sanitize(safeSource), DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(directory);

        string fileName = $"{DateTimeOffset.UtcNow:HHmmss}_{Guid.NewGuid():N}.wav";
        string path = Path.Combine(directory, fileName);

        await File.WriteAllBytesAsync(path, wav, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <inheritdoc />
    public async Task<int> RemoveLabeledAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Contracts.ClassificationEntry> labeled =
            await _journal.ListLabeledAsync(500, cancellationToken).ConfigureAwait(false);

        int removed = 0;

        foreach (Contracts.ClassificationEntry entry in labeled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.AudioPath) || !File.Exists(entry.AudioPath))
            {
                continue;
            }

            try
            {
                File.Delete(entry.AudioPath);
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить аудио {Path}", entry.AudioPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить аудио {Path}", entry.AudioPath);
            }
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<int> RemoveOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        int removed = 0;

        foreach (string path in Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            if (lastWrite >= cutoff.UtcDateTime)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить просроченное аудио {Path}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить просроченное аудио {Path}", path);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation(
                "Срок хранения аудио: удалено {Count} записей старше {Cutoff:yyyy-MM-dd}",
                removed,
                cutoff);
        }

        return removed;
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
