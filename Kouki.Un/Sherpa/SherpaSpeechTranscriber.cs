using System.Text;
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

        // Контракт ISpeechTranscriber не «пожелание»: под этот формат читается WAV
        // и настроена модель. Молча проигнорировать чужой формат — значит отдать мусор.
        if (format.Encoding is not (AudioEncoding.Wav or AudioEncoding.Pcm16LittleEndian))
        {
            throw new ArgumentException(
                $"Поддерживается только WAV/PCM16 ({AudioEncoding.Wav}), а в запросе {format.Encoding}.",
                nameof(format));
        }

        if (format.Channels != 1)
        {
            throw new ArgumentException(
                $"Поддерживается только моно, а в запросе {format.Channels} каналов.",
                nameof(format));
        }

        if (format.SampleRate != _options.SampleRate)
        {
            throw new ArgumentException(
                $"Ожидается частота {_options.SampleRate} Гц, а в запросе {format.SampleRate} Гц.",
                nameof(format));
        }

        float[] samples = WavPcmReader.ReadMono16KiloHertz(audio, _options.SampleRate);
        if (samples.Length == 0)
        {
            return Task.FromResult(TranscriptionResult.Empty);
        }

        OfflineRecognizer recognizer = _recognizer.Value;
        using OfflineStream stream = recognizer.CreateStream();

        stream.AcceptWaveform(_options.SampleRate, samples);
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
/// Минимальный разбор WAV по чанкам: PCM16, моно, заданная частота.
/// </summary>
/// <remarks>
/// Раньше «data» искался простым поиском байтов по всему файлу, и служебная строка
/// в самих сэмплах могла сдвинуть начало звука. Теперь идём по заголовку RIFF,
/// как положено, и проверяем формат, а не предполагаем его.
/// </remarks>
internal static class WavPcmReader
{
    public static float[] ReadMono16KiloHertz(Stream stream, int expectedSampleRate)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        if (bytes.Length < 12
            || bytes[0] != (byte)'R' || bytes[1] != (byte)'I'
            || bytes[2] != (byte)'F' || bytes[3] != (byte)'F'
            || bytes[8] != (byte)'W' || bytes[9] != (byte)'A'
            || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
        {
            throw new ArgumentException("Не WAV-файл: ожидался заголовок RIFF/WAVE.", nameof(stream));
        }

        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        int dataStart = -1;
        int dataLength = 0;

        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            int size = BitConverter.ToInt32(bytes, offset + 4);

            if (size < 0 || offset + 8 > bytes.Length - Math.Min(size, 0))
            {
                break;
            }

            if (chunkId == "fmt " && size >= 16 && offset + 8 + 16 <= bytes.Length)
            {
                channels = BitConverter.ToInt16(bytes, offset + 10);
                sampleRate = BitConverter.ToInt32(bytes, offset + 12);
                bitsPerSample = BitConverter.ToInt16(bytes, offset + 22);
            }
            else if (chunkId == "data")
            {
                dataStart = offset + 8;
                dataLength = Math.Min(size, Math.Max(0, bytes.Length - dataStart));
                break;
            }

            // Чанки выровнены по чётной границе.
            offset += 8 + size + (size & 1);
        }

        if (dataStart < 0 || dataLength < 2)
        {
            return Array.Empty<float>();
        }

        if (bitsPerSample != 16 || channels != 1 || sampleRate != expectedSampleRate)
        {
            throw new ArgumentException(
                $"Ожидается WAV PCM16, моно, {expectedSampleRate} Гц; в файле {bitsPerSample} бит, " +
                $"{channels} канал(ов), {sampleRate} Гц.",
                nameof(stream));
        }

        int sampleCount = dataLength / 2;
        var samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short value = BitConverter.ToInt16(bytes, dataStart + (i * 2));
            samples[i] = value / 32768f;
        }

        return samples;
    }
}
