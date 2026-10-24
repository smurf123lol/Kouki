namespace Kouki.Yasabi;

/// <summary>
/// Что сказал один классификатор по одной фразе. Для сравнения «роутеров»
/// между собой: правила, будущая BERT-модель, что-то ещё.
/// </summary>
/// <param name="Classifier">Имя классификатора, которым получен ответ.</param>
/// <param name="Intent">Предположенный интент или <see cref="IntentResult.Unclassified"/>.</param>
/// <param name="Score">Уверенность, 0…1.</param>
/// <param name="Confident">Прошёл ли ответ порог сравнения.</param>
/// <param name="Model">Модель внутри классификатора (`rules-v1`, например).</param>
/// <param name="Error">Текст ошибки, если классификатор упал.</param>
public sealed record ClassifierVerdict(
    string Classifier,
    string Intent,
    double Score,
    bool Confident,
    string Model,
    string? Error = null);

/// <summary>
/// Набор классификаторов, которые можно прогонять по одной фразе параллельно.
/// Нужен дашборду (вкладка «Проверка») и отладке: видно, где правила и модель расходятся.
/// </summary>
public interface IClassifierCatalog
{
    /// <summary>Имена зарегистрированных классификаторов.</summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>Ищет классификатор по имени (без учёта регистра).</summary>
    /// <param name="name">Имя классификатора.</param>
    /// <returns>Классификатор или <c>null</c>.</returns>
    IIntentClassifier? Find(string name);

    /// <summary>
    /// Прогоняет фразу через выбранные классификаторы. Ошибка одного не мешает остальным:
    /// она попадает в <see cref="ClassifierVerdict.Error"/>.
    /// </summary>
    /// <param name="text">Фраза.</param>
    /// <param name="only">Имена классификаторов; <c>null</c> — все.</param>
    /// <param name="threshold">Порог уверенности для <see cref="ClassifierVerdict.Confident"/>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<ClassifierVerdict>> ClassifyAsync(
        string text,
        IEnumerable<string>? only,
        double threshold,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Каталог классификаторов по именам. Регистрируется в <c>AddYasabi</c>.
/// </summary>
public sealed class ClassifierCatalog : IClassifierCatalog
{
    private readonly Dictionary<string, IIntentClassifier> _classifiers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Добавляет классификатор; повторное имя заменяет предыдущий.</summary>
    /// <param name="name">Имя для показа и выбора.</param>
    /// <param name="classifier">Классификатор.</param>
    /// <returns>Тот же каталог для цепочки.</returns>
    public ClassifierCatalog Add(string name, IIntentClassifier classifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(classifier);

        _classifiers[name.Trim()] = classifier;
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => _classifiers.Keys.ToArray();

    /// <inheritdoc />
    public IIntentClassifier? Find(string name)
        => name is not null && _classifiers.TryGetValue(name.Trim(), out IIntentClassifier? found)
            ? found
            : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClassifierVerdict>> ClassifyAsync(
        string text,
        IEnumerable<string>? only,
        double threshold,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        IEnumerable<KeyValuePair<string, IIntentClassifier>> selected = _classifiers;

        if (only is not null)
        {
            var wanted = new HashSet<string>(
                only.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()),
                StringComparer.OrdinalIgnoreCase);

            selected = _classifiers.Where(pair => wanted.Contains(pair.Key));
        }

        var verdicts = new List<ClassifierVerdict>();

        foreach ((string name, IIntentClassifier classifier) in selected)
        {
            try
            {
                IntentResult result = await classifier.ClassifyAsync(text, cancellationToken);
                verdicts.Add(new ClassifierVerdict(
                    name,
                    result.Intent,
                    result.Score,
                    result.IsConfident(threshold),
                    result.Model));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                verdicts.Add(new ClassifierVerdict(
                    name,
                    IntentResult.Unclassified,
                    0d,
                    false,
                    "error",
                    $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return verdicts;
    }
}
