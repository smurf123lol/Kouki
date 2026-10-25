using Kouki.Kagedama;
using Kouki.Kagedama.Hosting;

namespace Kouki.Kagedama.Api;

/// <summary>
/// Настройки HTTP-хоста.
/// </summary>
public sealed class KoukiServerOptions
{
    /// <summary>Имя секции в конфигурации (общее с остальными хостами).</summary>
    public const string SectionName = KoukiHostSettings.SectionName;

    /// <summary>
    /// Путь к файлу SQLite. Относительный разрешается от каталога приложения
    /// (<see cref="AppContext.BaseDirectory"/>), а не от текущего каталога процесса.
    /// </summary>
    public string DatabasePath { get; set; } = "kouki.sqlite";

    /// <summary>
    /// Запретить относительные пути к базе, аудио и модели: на сервере «рядом с dll»
    /// не должно быть сюрпризом. При включении относительный путь — ошибка запуска.
    /// </summary>
    public bool RequireAbsolutePaths { get; set; }

    /// <summary>
    /// Bearer-токен. Пустой токен закрывает API: открытый доступ включается только
    /// явным <see cref="AllowAnonymous"/>.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Разрешить работу без токена. Только для локальной отладки: при выезде за пределы
    /// своей машины API без токена доступен любому в сети.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Порог уверенности распознавания.</summary>
    public double ConfidenceThreshold { get; set; } = 0.75d;

    /// <summary>Хранить аудио голосовых запросов.</summary>
    public bool StoreAudio { get; set; } = true;

    /// <summary>Каталог для аудиозаписей.</summary>
    public string AudioDirectory { get; set; } = "audio";

    /// <summary>
    /// Срок хранения аудиозаписей в днях. По умолчанию 30.
    /// <c>0</c> или отрицательное значение — не удалять по возрасту (удаление по разметке остаётся).
    /// </summary>
    public int AudioRetentionDays { get; set; } = 30;

    /// <summary>
    /// Сколько дней хранить аудио после того, как его разметили, по умолчанию 90.
    /// </summary>
    /// <remarks>
    /// Размеченная запись — это датасет, но одновременно это живая речь человека.
    /// Срок короче общего: пока разметки нет, запись нужна как материал; после разметки
    /// задача выполнена, и держать голос дольше незачем. <c>0</c> — не удалять размеченное
    /// вовсе (например, если собираем датасет для обучения всерьёз).
    /// </remarks>
    public int AudioLabeledRetentionDays { get; set; } = 90;

    /// <summary>
    /// Максимальная длина текста во входных запросах (<c>text</c> у приёма и сравнения
    /// классификаторов), символов. Длинный текст — это уже не фраза: обрезать молча нельзя,
    /// поэтому такой запрос отклоняется.
    /// </summary>
    public int MaxTextLength { get; set; } = IntakeRequestPolicy.DefaultMaxTextLength;
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
