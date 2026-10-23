namespace Kouki.Kagedama.Api;

/// <summary>
/// Настройки HTTP-хоста.
/// </summary>
public sealed class KoukiServerOptions
{
    /// <summary>Имя секции в конфигурации.</summary>
    public const string SectionName = "Kouki";

    /// <summary>Путь к файлу SQLite. Относительный разрешается от рабочего каталога.</summary>
    public string DatabasePath { get; set; } = "kouki.sqlite";

    /// <summary>Bearer-токен. Пустая строка отключает проверку (только для локальной отладки).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Порог уверенности распознавания.</summary>
    public double ConfidenceThreshold { get; set; } = 0.75d;

    /// <summary>Хранить аудио голосовых запросов.</summary>
    public bool StoreAudio { get; set; } = true;

    /// <summary>Каталог для аудиозаписей.</summary>
    public string AudioDirectory { get; set; } = "audio";

    /// <summary>Максимальный размер голосовой записи, байт.</summary>
    public long MaxVoiceBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Максимальная длительность голосовой записи, секунд.</summary>
    public int MaxVoiceSeconds { get; set; } = 30;

    /// <summary>
    /// Каталог с моделью sherpa-onnx. Используется только в сборке
    /// с <c>-p:KoukiEnableSherpa=true</c>.
    /// </summary>
    public string SpeechModelDirectory { get; set; } = "models";

    /// <summary>
    /// Язык распознавания. По умолчанию русский; пустая строка — автоопределение.
    /// </summary>
    public string SpeechLanguage { get; set; } = "ru";
}
