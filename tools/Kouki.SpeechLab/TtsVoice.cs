using Kouki.SpeechLab;

#if KOUKI_SHERPA
using SherpaOnnx;
#endif

namespace Kouki.SpeechLab;

/// <summary>
/// Голос для проверок: синтез русской речи тем же sherpa-onnx, что и распознавание.
/// </summary>
/// <remarks>
/// Нужен, чтобы проверять конвейер на воспроизводимом входе: текст → речь → распознавание.
/// Датасет здесь не генерируется — это задача про качество модели, а не про работоспособность.
/// </remarks>
internal sealed class TtsVoice : IDisposable
{
#if KOUKI_SHERPA
    private readonly OfflineTts _tts;
    private readonly int _sampleRate;

    private TtsVoice(OfflineTts tts, int sampleRate)
    {
        _tts = tts;
        _sampleRate = sampleRate;
    }

    /// <summary>Готовит синтез по каталогу модели.</summary>
    public static TtsVoice Load(string directory)
    {
        string model = FindModel(directory);
        string tokens = Path.Combine(directory, "tokens.txt");
        string dataDir = Path.Combine(directory, "espeak-ng-data");

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = model;
        config.Model.Vits.Tokens = tokens;

        if (Directory.Exists(dataDir))
        {
            config.Model.Vits.DataDir = dataDir;
        }

        config.Model.NumThreads = 2;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;

        OfflineTts tts = new(config);
        return new TtsVoice(tts, tts.SampleRate);
    }

    /// <summary>Синтезирует фразу и пересчитывает в 16 кГц.</summary>
    public IReadOnlyList<float> Say(string text, float speed = 1f)
    {
        OfflineTtsGeneratedAudio audio = _tts.Generate(text, speed, speakerId: 0);
        float[] samples = audio.Samples;
        return WavWriter.Resample(samples, _sampleRate);
    }

    private static string FindModel(string directory)
    {
        string? model = Directory
            .GetFiles(directory, "*.onnx")
            .OrderByDescending(f => f.Contains("model", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (model is null)
        {
            throw new InvalidOperationException(
                $"В каталоге '{directory}' нет модели синтеза (*.onnx). Скачайте её: scripts\\fetch-tts-model.ps1");
        }

        return model;
    }

    public void Dispose() => _tts.Dispose();
#else
    private TtsVoice()
    {
    }

    public static TtsVoice Load(string directory) => new();

    public IReadOnlyList<float> Say(string text, float speed = 1f) =>
        throw new InvalidOperationException(
            "Синтез речи выключен: соберите с -p:KoukiEnableSherpa=true (пакет org.k2fsa.sherpa.onnx).");

    public void Dispose()
    {
    }
#endif
}
