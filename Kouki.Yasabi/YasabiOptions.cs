namespace Kouki.Yasabi;

/// <summary>
/// Настройки «Ясаби».
/// </summary>
public sealed class YasabiOptions
{
    /// <summary>
    /// Порог уверенности: ниже него действие не исполняется автоматически
    /// и запрос уходит в low-confidence.
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.75d;

    /// <summary>
    /// Источник, который пишется в журнал, если вызывающий его не указал.
    /// </summary>
    public string Source { get; set; } = "api";

    /// <summary>
    /// Правила классификатора. Пустой список означает <see cref="RuleIntentClassifier.DefaultRules"/>.
    /// </summary>
    public List<IntentRule> Rules { get; } = new();

    /// <summary>
    /// Считать сбой журнала фатальным. По умолчанию нет: журнал — наблюдаемость,
    /// он не должен отменять сохранение задачи.
    /// </summary>
    public bool JournalFailuresAreFatal { get; set; }
}
