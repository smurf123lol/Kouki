namespace Kouki.Un.Sherpa;

/// <summary>
/// Настройки распознавания речи через sherpa-onnx.
/// </summary>
/// <remarks>
/// Нужен каталог с моделью: <c>tokens.txt</c>, энкодер и декодер Whisper
/// (файлы <c>*encoder*.onnx</c> и <c>*decoder*.onnx</c>) — их раскладывает
/// скрипт <c>scripts/fetch-asr-model.ps1</c>. Пустые <see cref="Encoder"/>,
/// <see cref="Decoder"/> и <see cref="Tokens"/> заполняются при первом запуске
/// по содержимому каталога.
/// </remarks>
public sealed class SherpaOptions
{
    /// <summary>Каталог с моделью (например, <c>models/sherpa-onnx-whisper-small</c>).</summary>
    public string ModelDirectory { get; set; } = string.Empty;

    /// <summary>Путь к <c>tokens.txt</c>; по умолчанию ищется в каталоге модели.</summary>
    public string Tokens { get; set; } = string.Empty;

    /// <summary>Путь к энкодеру Whisper; по умолчанию первый <c>*encoder*.onnx</c>.</summary>
    public string Encoder { get; set; } = string.Empty;

    /// <summary>Путь к декодеру Whisper; по умолчанию первый <c>*decoder*.onnx</c>.</summary>
    public string Decoder { get; set; } = string.Empty;

    /// <summary>Язык распознавания. Для проекта — русский.</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Частота, с которой станция присылает звук.</summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>Размерность признаков модели (у Whisper — 80).</summary>
    public int FeatureDim { get; set; } = 80;

    /// <summary>Метод декодирования: <c>greedy_search</c> или <c>modified_beam_search</c>.</summary>
    public string DecodingMethod { get; set; } = "greedy_search";

    /// <summary>
    /// Дополняет незаполненные пути файлами из <see cref="ModelDirectory"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Модель не найдена целиком.</exception>
    public SherpaOptions Resolve()
    {
        if (string.IsNullOrWhiteSpace(ModelDirectory) || !Directory.Exists(ModelDirectory))
        {
            throw new InvalidOperationException(
                $"Модель распознавания не найдена: каталог '{ModelDirectory}' не существует. " +
                "Скачайте модель скриптом scripts/fetch-asr-model.ps1 и укажите Kouki:SpeechModelDirectory.");
        }

        Tokens = FirstOrExisting(Tokens, "*tokens.txt");
        Encoder = FirstOrExisting(Encoder, "*encoder*.onnx");
        Decoder = FirstOrExisting(Decoder, "*decoder*.onnx");

        return this;
    }

    private string FirstOrExisting(string configured, string pattern)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] matches = Directory.GetFiles(ModelDirectory, pattern, SearchOption.TopDirectoryOnly);
        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"В каталоге модели '{ModelDirectory}' нет файла '{pattern}'. " +
                "Проверьте, что модель распакована целиком.");
        }

        // В архиве лежат обе версии графа — fp32 и int8. int8 втрое меньше
        // по памяти и на процессоре заметно быстрее, а разница в тексте
        // для короткой фразы не слышна, поэтому по умолчанию берём её,
        // если файл вообще есть в каталоге.
        string? quantized = matches.FirstOrDefault(
            path => path.Contains(".int8.", StringComparison.OrdinalIgnoreCase));

        return quantized ?? matches.OrderBy(path => path, StringComparer.Ordinal).First();
    }
}
