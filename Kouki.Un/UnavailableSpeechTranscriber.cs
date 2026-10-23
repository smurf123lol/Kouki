using Kouki.Shimi.ServiceAbstractions;

namespace Kouki.Un;

/// <summary>
/// Слух «Ун» в состоянии заглушки: договор есть, распознавания пока нет.
/// Пакет <c>sherpa-onnx</c> подключается на этапе M4, после чего реализация
/// заменяется на <c>SherpaSpeechTranscriber</c> без изменения контракта.
/// </summary>
public sealed class UnavailableSpeechTranscriber : ISpeechTranscriber
{
    /// <summary>Текст причины, почему слуха нет.</summary>
    public const string Reason = "Распознавание речи ещё не подключено: пакет sherpa-onnx добавляется на этапе M4.";

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(Stream audio, AudioFormat format, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Reason);
}
