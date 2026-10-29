#if KOUKI_SHERPA
using System.Text;
using Kouki.Shimi.ServiceAbstractions;
using SherpaOnnx;

namespace Kouki.Un.Sherpa;

/// <summary>
/// Настройки синтеза речи (piper/vits через sherpa-onnx).
/// </summary>
public sealed class SherpaTtsOptions
{
    /// <summary>Каталог модели голоса: <c>*.onnx</c>, <c>tokens.txt</c>, <c>espeak-ng-data</c>.</summary>
    public string VoiceDirectory { get; set; } = string.Empty;

    /// <summary>Скорость речи: 1.0 — как задумал голос.</summary>
    public float Speed { get; set; } = 1.0f;

    /// <summary>Максимальная длина озвучиваемой фразы: длинные ответы станция не озвучивает.</summary>
    public int MaxTextLength { get; set; } = 400;
}

/// <summary>
/// Голос на sherpa-onnx: тот же набор библиотек, что и у распознавания,
/// поэтому отдельной зависимости не появляется.
/// </summary>
/// <remarks>
/// <see cref="OfflineTts"/> не потокобезопасен (внутри один граф), а станция и
/// бот могут попросить озвучивание одновременно — поэтому синтез идёт под семафором.
/// </remarks>
public sealed class SherpaSpeechSynthesizer : ISpeechSynthesizer, IDisposable
{
    /// <summary>Частота, которую ждут станция и бот (WAV PCM16 моно).</summary>
    public const int OutputSampleRate = 16000;

    private readonly OfflineTts? _tts;
    private readonly int _sourceRate;
    private readonly float _speed;
    private readonly int _maxTextLength;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Создаёт синтез по настройкам; если модель не найдена, объект остаётся
    /// работоспособным и объясняет причину через <see cref="UnavailableReason"/>.
    /// </summary>
    /// <param name="options">Настройки.</param>
    public SherpaSpeechSynthesizer(SherpaTtsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _speed = options.Speed <= 0 ? 1.0f : options.Speed;
        _maxTextLength = Math.Clamp(options.MaxTextLength, 1, 2000);

        if (!SpeechModelCheck.TryValidateVoice(options.VoiceDirectory, out string? problem))
        {
            UnavailableReason = problem;
            return;
        }

        string model = SpeechModelCheck.FindVoiceModel(options.VoiceDirectory)!;
        string tokens = Path.Combine(options.VoiceDirectory, "tokens.txt");
        string dataDirectory = Path.Combine(options.VoiceDirectory, "espeak-ng-data");

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = model;
        config.Model.Vits.Tokens = tokens;

        if (Directory.Exists(dataDirectory))
        {
            config.Model.Vits.DataDir = dataDirectory;
        }

        config.Model.NumThreads = 2;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;

        try
        {
            _tts = new OfflineTts(config);
            _sourceRate = _tts.SampleRate;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"не удалось загрузить голос: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public bool IsConfigured => _tts is not null;

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_tts is null)
        {
            throw new InvalidOperationException(UnavailableReason ?? "синтез речи не подключён");
        }

        string phrase = (text ?? string.Empty).Trim();

        if (phrase.Length == 0)
        {
            throw new ArgumentException("Нечего озвучивать: текст пуст.", nameof(text));
        }

        if (phrase.Length > _maxTextLength)
        {
            phrase = phrase[.._maxTextLength];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Генерация синхронная и на длинной фразе занимает сотни миллисекунд —
            // уводим её с потока запроса, чтобы станция не ждала ответа впустую.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                OfflineTtsGeneratedAudio audio = _tts.Generate(phrase, _speed, speakerId: 0);

                float[] samples = Resample(audio.Samples, _sourceRate, OutputSampleRate);
                byte[] wav = ToWav(samples, OutputSampleRate);

                return new SpeechSynthesisResult(wav, TimeSpan.FromSeconds((double)samples.Length / OutputSampleRate));
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// Линейная передискретизация: piper отдаёт 22 кГц, станция ждёт 16.
    /// </summary>
    /// <param name="samples">Исходные отсчёты.</param>
    /// <param name="from">Исходная частота.</param>
    /// <param name="to">Целевая частота.</param>
    public static float[] Resample(float[] samples, int from, int to)
    {
        if (from <= 0 || to <= 0 || samples.Length == 0 || from == to)
        {
            return samples;
        }

        int length = (int)Math.Round(samples.Length * (double)to / from);
        var result = new float[length];
        double step = (double)from / to;

        for (int i = 0; i < length; i++)
        {
            double position = i * step;
            int left = (int)position;
            int right = Math.Min(left + 1, samples.Length - 1);
            double fraction = position - left;
            result[i] = (float)(samples[left] * (1 - fraction) + samples[right] * fraction);
        }

        return result;
    }

    /// <summary>Собирает WAV PCM16 моно.</summary>
    /// <param name="samples">Отсчёты в диапазоне −1…1.</param>
    /// <param name="sampleRate">Частота.</param>
    public static byte[] ToWav(float[] samples, int sampleRate)
    {
        using var stream = new MemoryStream(44 + samples.Length * sizeof(short));
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        int dataLength = samples.Length * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);

        foreach (float sample in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
#endif
