using Kouki.Shimi.ServiceAbstractions;
using SherpaOnnx;

namespace Kouki.Un.Sherpa;

/// <summary>
/// Распознавание речи sherpa-onnx (модель Whisper, офлайн, без сети).
/// </summary>
/// <remarks>
/// Модель грузится лениво, при первой фразе: старт сервера не должен зависеть
/// от того, скачан ли трёхсотмегабайтный файл. Уверенность sherpa-onnx не
/// отдаёт, поэтому <see cref="TranscriptionResult.Confidence"/> равен <c>null</c>,
/// и решение «на разбор» принимает только классификатор интента.
/// </remarks>
public sealed class SherpaSpeechTranscriber : ISpeechTranscriber, IDisposable
{
    private readonly SherpaOptions _options;
    private readonly Lazy<OfflineRecognizer> _recognizer;

    public SherpaSpeechTranscriber(SherpaOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Resolve();
        _recognizer = new Lazy<OfflineRecognizer>(
            CreateRecognizer,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(
        Stream audio,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(format);
        cancellationToken.ThrowIfCancellationRequested();

        float[] samples = WavPcmReader.ReadMono16KiloHertz(audio);
        if (samples.Length == 0)
        {
            return Task.FromResult(TranscriptionResult.Empty);
        }

        OfflineRecognizer recognizer = _recognizer.Value;
        OfflineStream stream = recognizer.CreateStream();

        stream.AcceptWaveform(format.SampleRate <= 0 ? _options.SampleRate : format.SampleRate, samples);
        recognizer.Decode(stream);

        string text = stream.Result.Text?.Trim() ?? string.Empty;
        return Task.FromResult(new TranscriptionResult(text, null, Array.Empty<TranscriptionSegment>()));
    }

    private OfflineRecognizer CreateRecognizer()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = _options.SampleRate;
        config.FeatConfig.FeatureDim = _options.FeatureDim;

        config.ModelConfig.Tokens = _options.Tokens;
        config.ModelConfig.Whisper.Encoder = _options.Encoder;
        config.ModelConfig.Whisper.Decoder = _options.Decoder;
        config.ModelConfig.Whisper.Language = _options.Language;
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.Debug = 0;

        config.DecodingMethod = _options.DecodingMethod;

        return new OfflineRecognizer(config);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_recognizer.IsValueCreated)
        {
            _recognizer.Value.Dispose();
        }
    }
}

/// <summary>
/// Минимальный разбор WAV: PCM16, моно — именно так присылает станция.
/// </summary>
internal static class WavPcmReader
{
    private const int HeaderSize = 44;

    public static float[] ReadMono16KiloHertz(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        if (bytes.Length <= HeaderSize)
        {
            return Array.Empty<float>();
        }

        int dataStart = FindDataChunk(bytes);
        if (dataStart < 0)
        {
            return Array.Empty<float>();
        }

        int sampleCount = (bytes.Length - dataStart) / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short value = BitConverter.ToInt16(bytes, dataStart + (i * 2));
            samples[i] = value / 32768f;
        }

        return samples;
    }

    private static int FindDataChunk(byte[] bytes)
    {
        for (int i = 12; i + 8 <= bytes.Length; i++)
        {
            if (bytes[i] == (byte)'d' && bytes[i + 1] == (byte)'a'
                && bytes[i + 2] == (byte)'t' && bytes[i + 3] == (byte)'a')
            {
                return i + 8;
            }
        }

        return -1;
    }
}
