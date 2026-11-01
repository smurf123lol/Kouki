using System.Text.RegularExpressions;

namespace Kouki.Kagedama;

/// <summary>
/// Режим обращения с тегами во фразе.
/// </summary>
public static class TagModes
{
    /// <summary>
    /// Теги вырезаются из фразы и сохраняются отдельно (по умолчанию).
    /// </summary>
    public const string Extract = "extract";

    /// <summary>
    /// Теги остаются во фразе как часть формулировки, отдельно не сохраняются.
    /// </summary>
    public const string Keep = "keep";

    /// <summary>
    /// Проверяет, что режим известен.
    /// </summary>
    /// <param name="mode">Строка режима.</param>
    public static bool IsKnown(string? mode) =>
        string.Equals(mode, Extract, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, Keep, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Теги в фразе: <c>#работа</c>, <c>#проект/кухня</c>, <c>[срочно]</c>.
/// </summary>
/// <remarks>
/// Тег — это часть текста, которую человек пометил сам. Поэтому скрапинг детерминированный
/// (регулярка), а модель лишь дополняет набор своими тегами; формулировку задачи не трогаем
/// дальше, чем нужно, чтобы убрать сам тег.
/// </remarks>
public static partial class TagParser
{
    /// <summary>Максимум тегов на задачу: больше — это уже не метка, а простыня.</summary>
    public const int MaxTags = 10;

    /// <summary>Максимальная длина одного тега.</summary>
    public const int MaxTagLength = 32;

    [GeneratedRegex(@"#[\p{L}\p{N}_][\p{L}\p{N}_/\-]{0,31}|\[[\p{L}\p{N}_][\p{L}\p{N}_/\- ]{0,31}\]", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>
    /// Разбирает фразу: отдельно текст без тегов и отдельно сами теги.
    /// </summary>
    /// <param name="text">Исходная фраза.</param>
    /// <param name="mode">Режим: <see cref="TagModes.Extract"/> вырезает теги, <see cref="TagModes.Keep"/> — нет.</param>
    /// <returns>Текст для задачи и теги без повторов (в порядке появления, не больше <see cref="MaxTags"/>).</returns>
    /// <exception cref="ArgumentException">Режим неизвестен.</exception>
    public static (string Text, IReadOnlyList<string> Tags) Split(string? text, string? mode = TagModes.Extract)
    {
        string source = text ?? string.Empty;

        // Неизвестный режим — ошибка, а не «тихий keep»: иначе опечатка в конфиге
        // (например, extarct) сохраняет решётки в тексте и никто об этом не узнаёт.
        if (string.Equals(mode, TagModes.Keep, StringComparison.OrdinalIgnoreCase))
        {
            return (source, Array.Empty<string>());
        }

        if (!string.Equals(mode, TagModes.Extract, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Неизвестный режим тегов '{mode}'.", nameof(mode));
        }

        List<string> tags = new();
        foreach (Match match in TagPattern().Matches(source))
        {
            string tag = Normalize(match.Value);
            if (tag.Length == 0 || tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            tags.Add(tag);
            if (tags.Count == MaxTags)
            {
                break;
            }
        }

        if (tags.Count == 0)
        {
            return (source, Array.Empty<string>());
        }

        string cleaned = Clean(source);
        // Только теги и остались — тогда фразы нет, пусть решает приём (пустой текст отбьётся).
        return (cleaned, tags);
    }

    /// <summary>
    /// Убирает из фразы теги и лишние пробелы, не возвращая сами теги.
    /// </summary>
    /// <remarks>
    /// Нужен приёму: граница (<c>IntakeRequestPolicy</c>) уже вырезала теги, чтобы проверить
    /// «фраза не состоит из одних тегов», и передала их сервису — второй раз резать не нужно.
    /// </remarks>
    /// <param name="text">Фраза с тегами.</param>
    /// <returns>Фраза без тегов.</returns>
    public static string Clean(string? text) => Collapse(TagPattern().Replace(text ?? string.Empty, " "));

    /// <summary>
    /// Приводит тег к канонической форме: без <c>#</c> и скобок, в нижнем регистре.
    /// </summary>
    /// <param name="raw">Сырой тег из фразы (может быть с решёткой или в скобках).</param>
    public static string Normalize(string? raw)
    {
        string value = (raw ?? string.Empty).Trim();
        if (value.StartsWith('[') && value.EndsWith(']') && value.Length > 2)
        {
            value = value[1..^1];
        }

        value = value.TrimStart('#').Trim().ToLowerInvariant();

        // Пробелы внутри скобочных тегов заменяем на дефис: «[срочно важно]» → «срочно-важно».
        value = Regex.Replace(value, @"\s+", "-", RegexOptions.CultureInvariant);
        return value.Length > MaxTagLength ? value[..MaxTagLength] : value;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
