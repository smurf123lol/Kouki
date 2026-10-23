using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.Logging;

namespace Kouki.Kagedama;

/// <summary>
/// Результат голосового приёма.
/// </summary>
/// <param name="Transcription">Что услышало распознавание.</param>
/// <param name="Intake">Что из этого понял приём задач; <c>null</c>, если фразу распознать не удалось.</param>
/// <param name="AudioPath">Куда сохранена запись, если сохраняли.</param>
/// <param name="Error">Почему не получилось, если не получилось.</param>
public sealed record VoiceIntakeResult(
    TranscriptionResult? Transcription,
    IntakeResult? Intake,
    string? AudioPath,
    string? Error);

/// <summary>
/// Голосовой приём: звук → текст → та же дорога, что у обычной фразы.
/// </summary>
/// <remarks>
/// Здесь только склейка: распознавание (Ун) и понимание (Ясаби) не знают друг о друге,
/// а приём задач остаётся единственной точкой входа для фраз.
/// Порога на уверенность распознавания нет намеренно: sherpa-onnx её пока не отдаёт,
/// а решение «на разбор» принимает классификатор интента — он отвечает за смысл,
/// а не за акустику.
/// </remarks>
public sealed class VoiceIntakeService
{
    private readonly ISpeechTranscriber _transcriber;
    private readonly TaskIntakeService _intake;
    private readonly IAudioStore? _audioStore;
    private readonly ILogger<VoiceIntakeService> _logger;

    /// <summary>
    /// Создаёт сервис голосового приёма.
    /// </summary>
    /// <param name="transcriber">Распознавание речи.</param>
    /// <param name="intake">Приём фраз.</param>
    /// <param name="logger">Лог.</param>
    /// <param name="audioStore">Хранилище записей; <c>null</c> — не сохранять.</param>
    public VoiceIntakeService(
        ISpeechTranscriber transcriber,
        TaskIntakeService intake,
        ILogger<VoiceIntakeService> logger,
        IAudioStore? audioStore = null)
    {
        ArgumentNullException.ThrowIfNull(transcriber);
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(logger);

        _transcriber = transcriber;
        _intake = intake;
        _logger = logger;
        _audioStore = audioStore;
    }

    /// <summary>
    /// Принимает запись: распознаёт речь, сохраняет аудио и передаёт текст в приём задач.
    /// </summary>
    /// <param name="wav">Байты WAV.</param>
    /// <param name="source">Источник: station, dashboard, telegram.</param>
    /// <param name="format">Формат записи.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<VoiceIntakeResult> AcceptAsync(
        byte[] wav,
        string source,
        AudioFormat format,
        CancellationToken cancellationToken = default,
        string? intentHint = null)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        TranscriptionResult transcription;

        try
        {
            using var stream = new MemoryStream(wav, writable: false);
            transcription = await _transcriber.TranscribeAsync(stream, format, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Распознавание речи не удалось (источник {Source})", source);
            return new VoiceIntakeResult(null, null, null, $"Распознавание не удалось: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(transcription.Text))
        {
            return new VoiceIntakeResult(transcription, null, null, "Речь не распознана.");
        }

        string? audioPath = null;

        if (_audioStore is not null)
        {
            try
            {
                audioPath = await _audioStore.SaveAsync(wav, source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Запись важнее архива: не сохранили аудио — всё равно обрабатываем фразу.
                _logger.LogWarning(ex, "Не удалось сохранить аудио (источник {Source})", source);
            }
        }

        // Уверенность распознавания (если модель её отдаёт) кладётся в мету задачи и журнал,
        // но не влияет на маршрут: за смысл отвечает классификатор интента.
        IntakeResult intake = await _intake.IntakeAsync(
            transcription.Text,
            source,
            cancellationToken,
            transcription.Confidence,
            audioPath,
            intentHint).ConfigureAwait(false);

        return new VoiceIntakeResult(transcription, intake, audioPath, null);
    }
}
