using System.Text.RegularExpressions;

namespace Kouki.Yasabi;

/// <summary>
/// Правило сопоставления фразы с интентом. Сильное правило даёт 1.0,
/// слабое — 0.5: так появляются пограничные случаи, которые попадают в журнал
/// как low-confidence и становятся будущим датасетом для модели.
/// </summary>
public sealed class IntentRule
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Создаёт правило.
    /// </summary>
    /// <param name="intent">Имя интента.</param>
    /// <param name="pattern">Регулярное выражение.</param>
    /// <param name="weight">Уверенность при совпадении (0..1).</param>
    public IntentRule(string intent, string pattern, double weight = 1d)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 1d);

        Intent = intent;
        Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout);
        Weight = weight;
    }

    /// <summary>Имя интента.</summary>
    public string Intent { get; }

    /// <summary>Регулярное выражение правила.</summary>
    public Regex Pattern { get; }

    /// <summary>Уверенность при совпадении.</summary>
    public double Weight { get; }

    /// <summary>Совпадает ли фраза с правилом.</summary>
    /// <param name="text">Текст запроса.</param>
    public bool TryMatch(string text) => Pattern.IsMatch(text);


    /// <summary>Сильное правило: явная формулировка.</summary>
    public static IntentRule Strong(string intent, string pattern) => new(intent, pattern, 1d);

    /// <summary>Слабое правило: намёк, которого мало для исполнения.</summary>
    public static IntentRule Weak(string intent, string pattern) => new(intent, pattern, 0.5d);
}
