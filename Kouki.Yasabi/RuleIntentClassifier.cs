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

        // Напоминания. Сильные правила требуют явного глагола: «напомни …»,
        // «поставь напоминание …». Слово «напоминание» само по себе — слабый
        // признак: «добавь задачу напомнить маме» остаётся задачей.
        IntentRule.Strong("add_reminder", @"^\s*напомн(и|ить|и-ка)\b"),
        IntentRule.Strong("add_reminder", @"^\s*(поставь|создай|сделай)\s+напоминани\w*"),
        IntentRule.Strong("add_reminder", @"\b(remind me|set a reminder)\b"),
        IntentRule.Weak("add_reminder", @"\bнапоминани\w*"),
        IntentRule.Strong("list_reminders", @"(покажи|список|какие|что)\s+(?:у\s+меня\s+|мои\s+|все\s+|все\s+мои\s+)*(напоминани\w*)"),
        IntentRule.Strong("complete_reminder", @"\b(готово|выполнил\w*|сделал\w*)\s+(?:с\s+)?напоминани\w*"),

        // Вопрос. Правило стоит последним и потому проигрывает по весу явным
        // командам выше: «Покажи мои задачи?» остаётся `list_tasks`, а
        // «Запиши задачу купить хлеб?» — `add_task`. Знак вопроса сам по себе
        // слабый признак (0.5): «это же задача?» — не обязательно вопрос к модели.
        IntentRule.Strong("ask", @"^\s*(что|сколько|как|какой|какая|какие|каков|где|когда|кто|почему|зачем|чем|куда|откуда)\b"),
        IntentRule.Strong("ask", @"^\s*(можно|есть|правда|верно|нужно)\s+ли\b"),
        IntentRule.Strong("ask", @"^\s*(спроси|вопрос)\b"),
        IntentRule.Strong("ask", @"^\s*(what|how|why|when|where|who|which|whose|is|are|can|could|should|does|do|did)\b"),
        IntentRule.Weak("ask", @"\?\s*$"),
        // Вопрос «наоборот»: «гугол это сколько», «а как же ужин». Вопросительное
        // слово может стоять не первым, поэтому вес ниже явной команды (0.8):
        // «запиши задачу, когда будет время» остаётся задачей.
        new IntentRule("ask", @"\b(сколько|почему|зачем|какой|какая|какие|каков|куда|откуда)\b", 0.8d),
    };
}
