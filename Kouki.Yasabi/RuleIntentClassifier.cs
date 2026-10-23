using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kouki.Yasabi;

/// <summary>
/// Классификатор на правилах: первый, самый дешёвый шаг «Ясаби».
/// Не требует модели, работает офлайн и служит эталоном для будущего BERT.
/// </summary>
public sealed class RuleIntentClassifier : IIntentClassifier
{
    /// <summary>Имя набора правил для журнала.</summary>
    public const string ModelName = "rules-v1";

    private readonly IReadOnlyList<IntentRule> _rules;

    /// <summary>
    /// Создаёт классификатор.
    /// </summary>
    /// <param name="rules">Правила; при пустом списке используются <see cref="DefaultRules"/>.</param>
    public RuleIntentClassifier(IEnumerable<IntentRule>? rules = null)
    {
        _rules = rules is null ? DefaultRules : rules.ToArray();
    }

    /// <summary>Правила классификатора.</summary>
    public IReadOnlyList<IntentRule> Rules => _rules;

    /// <inheritdoc />
    public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(IntentResult.Unknown(ModelName));
        }

        IntentRule? best = null;

        foreach (IntentRule rule in _rules)
        {
            if (!rule.TryMatch(text))
            {
                continue;
            }

            if (best is null || rule.Weight > best.Weight)
            {
                best = rule;
            }
        }

        IntentResult result = best is null
            ? IntentResult.Unknown(ModelName)
            : new IntentResult(best.Intent, best.Weight, null, ModelName);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Стартовый набор правил для задач: сильные формулировки дают 1.0,
    /// одиночные слова — 0.5 и попадают в low-confidence.
    /// </summary>
    public static IReadOnlyList<IntentRule> DefaultRules { get; } = new[]
    {
        IntentRule.Strong("add_task", @"(запиши|записать|добавь|сохрани|поставь)\s+(мне\s+)?(задач\w*|дел\w*|таск\w*)"),
        IntentRule.Strong("add_task", @"\b(add|create)\s+(a\s+)?task\b"),
        IntentRule.Weak("add_task", @"задач"),
        IntentRule.Strong("list_tasks", @"(покажи|список|какие|что)\s+(?:у\s+меня\s+|мои\s+|все\s+|все\s+мои\s+)*(задач\w*|дел\w*)"),
        IntentRule.Strong("list_tasks", @"\b(list|show)\s+(my\s+)?tasks?\b"),
        IntentRule.Strong("complete_task", @"(готово|выполнил\w*|закрой|заверши\w*|сделал\w*)\s+(задач\w*)"),
        IntentRule.Weak("complete_task", @"^(готово|выполнил\w*|сделал\w*)$"),
    };
}
