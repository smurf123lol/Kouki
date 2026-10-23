namespace Kouki.Shimi.ServiceAbstractions;

/// <summary>
/// Способ кодирования аудиопотока.
/// </summary>
public enum AudioEncoding
{
    /// <summary>Сырые 16-битные отсчёты little-endian.</summary>
    Pcm16LittleEndian,

    /// <summary>Контейнер WAV.</summary>
    Wav,

    /// <summary>Opus в контейнере Ogg.</summary>
    Opus,

    /// <summary>MP3.</summary>
    Mp3,
}

/// <summary>
/// Описание аудиопотока, который принимает распознаватель.
/// </summary>
/// <param name="SampleRate">Частота дискретизации в герцах.</param>
/// <param name="Channels">Число каналов.</param>
/// <param name="Encoding">Кодирование отсчётов.</param>
public sealed record AudioFormat(int SampleRate, int Channels, AudioEncoding Encoding)
{
    /// <summary>Формат голосовых запросов: WAV, 16 кГц, моно.</summary>
    public static AudioFormat Wav16KiloHertzMono { get; } = new(16000, 1, AudioEncoding.Wav);
}

/// <summary>
/// Распознанный фрагмент речи с привязкой ко времени.
/// </summary>
/// <param name="Start">Начало фрагмента.</param>
/// <param name="End">Конец фрагмента.</param>
/// <param name="Text">Распознанный текст.</param>
public sealed record TranscriptionSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Результат распознавания речи.
/// </summary>
/// <param name="Text">Полный текст записи.</param>
/// <param name="Confidence">Уверенность распознавателя, если он её сообщает.</param>
/// <param name="Segments">Разбивка по времени.</param>
public sealed record TranscriptionResult(string Text, double? Confidence, IReadOnlyList<TranscriptionSegment> Segments)
{
    /// <summary>Пустой результат: речи не обнаружено.</summary>
    public static TranscriptionResult Empty { get; } = new(string.Empty, null, Array.Empty<TranscriptionSegment>());
}

/// <summary>
/// Слух: превращает аудио в текст (муси Ун). Абстракция живёт здесь,
/// реализация — в <c>Kouki.Un</c>, как <see cref="IFileSystem"/> и её адаптеры.
/// </summary>
public interface ISpeechTranscriber
{
    /// <summary>
    /// Распознаёт речь в потоке.
    /// </summary>
    /// <param name="audio">Аудиоданные.</param>
    /// <param name="format">Формат данных.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат распознавания.</returns>
    /// <exception cref="InvalidOperationException">Распознаватель не подключён или не умеет этот формат.</exception>
    Task<TranscriptionResult> TranscribeAsync(Stream audio, AudioFormat format, CancellationToken cancellationToken = default);
}
