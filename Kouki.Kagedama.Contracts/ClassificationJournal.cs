namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Одна запись журнала распознавания: сырой ввод, вердикт, уверенность и ошибка.
/// Из этих записей потом собирается датасет для BERT.
/// </summary>
/// <param name="Source">Откуда пришёл запрос: telegram, voice, dashboard, mcp, api.</param>
/// <param name="InputKind">Вид ввода: text или asr.</param>
/// <param name="InputText">Текст, попавший в классификатор.</param>
/// <param name="Intent">Распознанный интент.</param>
/// <param name="Score">Уверенность распознавания.</param>
/// <param name="Model">Модель или набор правил.</param>
/// <param name="Confident">Превышен ли порог.</param>
/// <param name="Error">Текст ошибки, если классификация упала.</param>
/// <param name="AsrConfidence">Уверенность распознавания речи, если ввод был голосовым.</param>
/// <param name="AudioPath">Путь к сохранённой записи, если она есть.</param>
/// <param name="CreatedAt">Момент записи.</param>
public sealed record ClassificationRecord(
    string Source,
    string InputKind,
    string InputText,
    string Intent,
    double Score,
    string Model,
    bool Confident,
    string? Error,
    double? AsrConfidence = null,
    string? AudioPath = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// Журнал распознавания: хранилище low-confidence кейсов и ошибок.
/// </summary>
public interface IClassificationJournal
{
    /// <summary>
    /// Записывает результат распознавания.
    /// </summary>
    /// <param name="record">Запись.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Идентификатор записи журнала.</returns>
    Task<Guid> RecordAsync(ClassificationRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Последние записи журнала.
    /// </summary>
    /// <param name="limit">Сколько вернуть.</param>
    /// <param name="onlyUnresolved">Только те, что ещё не размечены: неуверенные или упавшие.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<ClassificationEntry>> ListAsync(int limit, bool onlyUnresolved, CancellationToken cancellationToken = default);

    /// <summary>
    /// Дописывает к записи данные распознавания речи: уверенность и путь к аудио.
    /// </summary>
    /// <param name="id">Запись журнала.</param>
    /// <param name="asrConfidence">Уверенность распознавания речи.</param>
    /// <param name="audioPath">Путь к сохранённой записи.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><c>true</c>, если запись нашлась.</returns>
    Task<bool> AnnotateAudioAsync(Guid id, double? asrConfidence, string? audioPath, CancellationToken cancellationToken = default, string? source = null);

    /// <summary>
    /// Переписывает вердикт записи, когда интент пришёл не от классификатора, а из сессии
    /// (кнопка «Записать»/«Спросить» на станции).
    /// </summary>
    /// <remarks>
    /// Классификатор пишет свою догадку (часто <c>unclassified</c>), а сессия знает интент точно.
    /// Без этой правки фильтр «что не распознали» показывал бы и удачные записи.
    /// </remarks>
    /// <param name="id">Запись журнала.</param>
    /// <param name="intent">Интент сессии.</param>
    /// <param name="model">Модель с пометкой сессии, например <c>rules-v1+session</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><c>true</c>, если запись нашлась.</returns>
    Task<bool> AnnotateIntentAsync(Guid id, string intent, string model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Записи, которые уже размечены вручную: их аудио можно считать отработанным.
    /// </summary>
    /// <param name="limit">Сколько вернуть.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<ClassificationEntry>> ListLabeledAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ставит ручную метку интента — материал для датасета.
    /// </summary>
    /// <param name="id">Запись журнала.</param>
    /// <param name="label">Правильный интент.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<bool> LabelAsync(Guid id, string label, CancellationToken cancellationToken = default);
}

/// <summary>
/// Запись журнала в том виде, в котором её видит дашборд.
/// </summary>
public sealed class ClassificationEntry
{
    /// <summary>Идентификатор записи.</summary>
    public Guid Id { get; set; }

    /// <summary>Момент записи.</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>Источник запроса.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Вид ввода.</summary>
    public string InputKind { get; set; } = string.Empty;

    /// <summary>Текст запроса.</summary>
    public string InputText { get; set; } = string.Empty;

    /// <summary>Распознанный интент.</summary>
    public string Intent { get; set; } = string.Empty;

    /// <summary>Уверенность.</summary>
    public double Score { get; set; }

    /// <summary>Превышен ли порог.</summary>
    public bool Confident { get; set; }

    /// <summary>Модель.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Ошибка.</summary>
    public string? Error { get; set; }

    /// <summary>Ручная метка.</summary>
    public string? Label { get; set; }

    /// <summary>Момент разметки.</summary>
    public string? LabeledAt { get; set; }

    /// <summary>Уверенность распознавания речи, если ввод был голосовым.</summary>
    public double? AsrConfidence { get; set; }

    /// <summary>Путь к сохранённой записи, если она есть.</summary>
    public string? AudioPath { get; set; }
}

/// <summary>
/// Журнал-пустышка: нужен, когда хранилища нет (тесты, консоль).
/// </summary>
public sealed class NullClassificationJournal : IClassificationJournal
{
    /// <summary>Единственный экземпляр.</summary>
    public static NullClassificationJournal Instance { get; } = new();

    /// <inheritdoc />
    public Task<Guid> RecordAsync(ClassificationRecord record, CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.Empty);

    /// <inheritdoc />
    public Task<IReadOnlyList<ClassificationEntry>> ListAsync(int limit, bool onlyUnresolved, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ClassificationEntry>>(Array.Empty<ClassificationEntry>());

    /// <inheritdoc />
    public Task<bool> LabelAsync(Guid id, string label, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> AnnotateAudioAsync(Guid id, double? asrConfidence, string? audioPath, CancellationToken cancellationToken = default, string? source = null)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<ClassificationEntry>> ListLabeledAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ClassificationEntry>>(Array.Empty<ClassificationEntry>());

    /// <inheritdoc />
    public Task<bool> AnnotateIntentAsync(Guid id, string intent, string model, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
