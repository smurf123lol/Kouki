namespace Kouki.Yasabi;

/// <summary>
/// Интент «правила, а где не поняли — модель»: быстрый путь остаётся быстрым,
/// а незнакомые формулировки разбирает языковая модель.
/// </summary>
/// <remarks>
/// Правила — это список, который поддерживают руками: он ловит ровно те формы, под которые
/// его написали, и отвечает за микросекунды. Модель понимает «надо бы не забыть сходить
/// в аптеку», но тратит секунды. Спрашивать модель только при неуверенности правил дешевле,
/// чем звать её всегда, и точнее, чем одни правила. Кто именно ответил, видно в поле
/// <see cref="IntentResult.Model"/> (<c>rules-v1</c> или <c>llm</c>) — журнал остаётся честным.
/// </remarks>
public sealed class HybridIntentClassifier : IIntentClassifier
{
    /// <summary>Имя связки в каталоге роутеров.</summary>
    public const string ModelName = "rules+llm";

    private readonly IIntentClassifier _rules;
    private readonly IIntentClassifier _llm;
    private readonly double _fallbackBelow;

    /// <summary>
    /// Создаёт связку правил и модели.
    /// </summary>
    /// <param name="rules">Быстрый классификатор правилами.</param>
    /// <param name="llm">Классификатор языковой моделью.</param>
    /// <param name="fallbackBelow">Порог уверенности правил, ниже которого спрашиваем модель (0…1).</param>
    public HybridIntentClassifier(IIntentClassifier rules, IIntentClassifier llm, double fallbackBelow = 0.95d)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(llm);

        _rules = rules;
        _llm = llm;
        _fallbackBelow = Math.Clamp(fallbackBelow, 0d, 1d);
    }

    /// <inheritdoc />
    public async Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
    {
        IntentResult fromRules = await _rules.ClassifyAsync(text, cancellationToken).ConfigureAwait(false);

        if (fromRules.Intent != IntentResult.Unclassified && fromRules.Score >= _fallbackBelow)
        {
            return fromRules;
        }

        IntentResult fromLlm = await _llm.ClassifyAsync(text, cancellationToken).ConfigureAwait(false);

        // Модель молчит (недоступна или ответила мусором) — отдаём то, что поняли правила,
        // чтобы приём не зависел от состояния llama-server.
        if (fromLlm.Intent == IntentResult.Unclassified)
        {
            return fromRules;
        }

        return fromLlm;
    }
}
