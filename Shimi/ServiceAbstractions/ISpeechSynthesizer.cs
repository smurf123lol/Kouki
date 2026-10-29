namespace Kouki.Shimi.ServiceAbstractions;

/// <summary>Результат синтеза речи.</summary>
/// <param name="Wav">Готовый WAV PCM16 моно 16 кГц — ровно тот формат, что ждут станция и бот.</param>
/// <param name="Duration">Длительность звучания.</param>
public sealed record SpeechSynthesisResult(byte[] Wav, TimeSpan Duration);

/// <summary>
/// Голос: превращает текст в речь. Пара к <see cref="ISpeechTranscriber"/>:
/// станция и бот не должны знать, чем именно синтезируется звук.
/// </summary>
public interface ISpeechSynthesizer
{
    /// <summary>Синтез подключён и готов работать.</summary>
    bool IsConfigured { get; }

    /// <summary>Почему синтез недоступен (для лога и ответа <c>503</c>).</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Синтезирует фразу в WAV 16 кГц моно.
    /// </summary>
    /// <param name="text">Текст для озвучивания.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Звук и его длительность.</returns>
    /// <exception cref="InvalidOperationException">Синтез не подключён.</exception>
    Task<SpeechSynthesisResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}
