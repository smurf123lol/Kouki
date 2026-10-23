namespace Kouki.SpeechLab;

/// <summary>
/// Тонкая обёртка над распознавателем из <c>Kouki.Un</c>: тот же код, что в сервере.
/// </summary>
internal sealed class Transcriber : IDisposable
{
#if KOUKI_SHERPA
    private readonly Kouki.Un.Sherpa.SherpaSpeechTranscriber _inner;

    public Transcriber(string modelDirectory, string language)
    {
        var options = new Kouki.Un.Sherpa.SherpaOptions
        {
            ModelDirectory = modelDirectory,
            Language = language,
        };

        _inner = new Kouki.Un.Sherpa.SherpaSpeechTranscriber(options);
    }

    /// <summary>Распознаёт WAV PCM16 16 кГц моно.</summary>
    public TranscriptionReport Transcribe(byte[] wav)
    {
        using var stream = new MemoryStream(wav, writable: false);
        Kouki.Shimi.ServiceAbstractions.TranscriptionResult result = _inner
            .TranscribeAsync(stream, Kouki.Shimi.ServiceAbstractions.AudioFormat.Wav16KiloHertzMono)
            .GetAwaiter()
            .GetResult();

        return new TranscriptionReport(result.Text.Trim());
    }

    public void Dispose() => _inner.Dispose();
#else
    public Transcriber(string modelDirectory, string language) => throw new InvalidOperationException(
        "Распознавание выключено: соберите с -p:KoukiEnableSherpa=true (пакет org.k2fsa.sherpa.onnx).");

    public TranscriptionReport Transcribe(byte[] wav) => throw new NotSupportedException();

    public void Dispose()
    {
    }
#endif
}

/// <summary>Что услышали.</summary>
internal sealed record TranscriptionReport(string Text);
