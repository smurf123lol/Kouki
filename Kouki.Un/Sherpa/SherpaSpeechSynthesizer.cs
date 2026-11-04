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
    /// <summary>Базовый каталог голосов: содержит <c>vits-piper-*</c> или файлы модели.</summary>
    public string VoiceDirectory { get; set; } = string.Empty;

    /// <summary>Голос по умолчанию.</summary>
    public string DefaultVoice { get; set; } = "ru_RU-irina-medium";

    /// <summary>Максимальное число загруженных в память моделей голосов (LRU).</summary>
    public int MaxLoadedVoices { get; set; } = 2;

    /// <summary>Скорость речи: 1.0 — как задумал голос.</summary>
    public float Speed { get; set; } = 1.0f;

    /// <summary>Максимальная длина озвучиваемой фразы: длинные ответы станция не озвучивает.</summary>
    public int MaxTextLength { get; set; } = 400;
}

/// <summary>
/// Голос на sherpa-onnx: тот же набор библиотек, что и у распознавания,
/// поэтому отдельной зависимости не появляется. Поддерживает несколько тембров
/// с ленивой загрузкой и LRU-вытеснением.
/// </summary>
/// <remarks>
/// <see cref="OfflineTts"/> не потокобезопасен (внутри один граф), а станция и
/// бот могут попросить озвучивание одновременно — поэтому синтез идёт под семафором.
/// </remarks>
public sealed class SherpaSpeechSynthesizer : ISpeechSynthesizer, IDisposable
{
    /// <summary>Частота, которую ждут станция и бот (WAV PCM16 моно).</summary>
    public const int OutputSampleRate = 16000;

    private sealed class LoadedVoice : IDisposable
    {
        public required string Name { get; init; }
        public required OfflineTts Tts { get; init; }
        public required int SourceRate { get; init; }
        public DateTime LastUsed { get; set; }

        public void Dispose()
        {
            (Tts as IDisposable)?.Dispose();
        }
    }

    private readonly Dictionary<string, LoadedVoice> _loadedVoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _baseVoiceDirectory;
    private readonly string _defaultVoice;
    private readonly int _maxLoadedVoices;
    private readonly float _speed;
    private readonly int _maxTextLength;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Создаёт синтез по настройкам; если модель по умолчанию не найдена, объект остаётся
    /// работоспособным и объясняет причину через <see cref="UnavailableReason"/>.
    /// </summary>
    /// <param name="options">Настройки.</param>
    public SherpaSpeechSynthesizer(SherpaTtsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _baseVoiceDirectory = options.VoiceDirectory ?? string.Empty;
        _defaultVoice = string.IsNullOrWhiteSpace(options.DefaultVoice) ? "ru_RU-irina-medium" : options.DefaultVoice.Trim();
        _maxLoadedVoices = Math.Max(1, options.MaxLoadedVoices);
        _speed = options.Speed <= 0 ? 1.0f : options.Speed;
        _maxTextLength = Math.Clamp(options.MaxTextLength, 1, 2000);

        string? defaultDir = SpeechVoiceResolver.ResolveVoiceDirectory(
            _baseVoiceDirectory,
            _defaultVoice,
            _defaultVoice);

        if (defaultDir is null)
        {
            UnavailableReason = $"голос по умолчанию '{_defaultVoice}' не найден в '{_baseVoiceDirectory}'";
            return;
        }

        try
        {
            var (tts, sourceRate) = CreateOfflineTts(defaultDir);
            _loadedVoices[_defaultVoice] = new LoadedVoice
            {
                Name = _defaultVoice,
                Tts = tts,
                SourceRate = sourceRate,
                LastUsed = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            UnavailableReason = $"не удалось загрузить голос '{_defaultVoice}': {ex.Message}";
        }
    }

    /// <inheritdoc />
    public bool IsConfigured => _loadedVoices.ContainsKey(_defaultVoice);

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default)
        => SynthesizeAsync(text, null, cancellationToken);

    /// <inheritdoc />
    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        string? voice,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConfigured)
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

        string targetVoice = string.IsNullOrWhiteSpace(voice) ? _defaultVoice : voice.Trim();

        if (!string.Equals(targetVoice, _defaultVoice, StringComparison.OrdinalIgnoreCase))
        {
            if (!SpeechVoiceResolver.TryValidateVoiceName(targetVoice, out string? nameError))
            {
                throw new ArgumentException(nameError, nameof(voice));
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loadedVoices.TryGetValue(targetVoice, out LoadedVoice? loaded))
            {
                string? voiceDir = SpeechVoiceResolver.ResolveVoiceDirectory(
                    _baseVoiceDirectory,
                    targetVoice,
                    _defaultVoice);

                if (voiceDir is null)
                {
                    IReadOnlyList<string> available = SpeechVoiceResolver.EnumerateAvailableVoices(
                        _baseVoiceDirectory,
                        _defaultVoice);
                    throw new ArgumentException(
                        SpeechVoiceResolver.FormatVoiceNotFoundMessage(targetVoice, available),
                        nameof(voice));
                }

                try
                {
                    var (tts, sourceRate) = CreateOfflineTts(voiceDir);
                    loaded = new LoadedVoice
                    {
                        Name = targetVoice,
                        Tts = tts,
                        SourceRate = sourceRate,
                        LastUsed = DateTime.UtcNow
                    };

                    if (_loadedVoices.Count >= _maxLoadedVoices)
                    {
                        var oldestNonDefault = _loadedVoices
                            .Where(kv => !string.Equals(kv.Key, _defaultVoice, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(kv => kv.Value.LastUsed)
                            .Select(kv => kv.Key)
                            .FirstOrDefault();

                        if (oldestNonDefault is not null && _loadedVoices.Remove(oldestNonDefault, out LoadedVoice? evicted))
                        {
                            evicted.Dispose();
                        }
                        else if (_maxLoadedVoices <= 1 && _loadedVoices.Remove(_defaultVoice, out LoadedVoice? evictedDefault))
                        {
                            evictedDefault.Dispose();
                        }
                    }

                    _loadedVoices[targetVoice] = loaded;
                }
                catch (Exception ex) when (ex is not ArgumentException)
                {
                    throw new InvalidOperationException($"не удалось загрузить голос '{targetVoice}': {ex.Message}", ex);
                }
            }

            loaded.LastUsed = DateTime.UtcNow;

            var audio = loaded.Tts.Generate(phrase, _speed, speakerId: 0);
            float[] samples = Resample(audio.Samples, loaded.SourceRate, OutputSampleRate);
            byte[] wav = ToWav(samples, OutputSampleRate);
            TimeSpan duration = TimeSpan.FromSeconds((double)samples.Length / OutputSampleRate);

            return new SpeechSynthesisResult(wav, duration);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (OfflineTts Tts, int SourceRate) CreateOfflineTts(string voiceDirectory)
    {
        string model = SpeechModelCheck.FindVoiceModel(voiceDirectory)
            ?? throw new InvalidOperationException($"В каталоге {voiceDirectory} нет модели *.onnx");
        string tokens = Path.Combine(voiceDirectory, "tokens.txt");
        string dataDirectory = Path.Combine(voiceDirectory, "espeak-ng-data");

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

        var tts = new OfflineTts(config);
        return (tts, tts.SampleRate);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (LoadedVoice voice in _loadedVoices.Values)
        {
            voice.Dispose();
        }
        _loadedVoices.Clear();
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
