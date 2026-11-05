using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kouki.Kagedama.Contracts;

namespace Kouki.Kotae;

/// <summary>
/// Подсказка для модели и уборка ответа.
/// </summary>
/// <remarks>
/// Зачем: модель не знает сегодняшнюю дату, а на вопрос «какое сегодня число» уверенно
/// выдаёт дату из обучения. Поэтому дата и часовой пояс подставляются в системное
/// сообщение при каждом запросе. Уборка нужна голосовой станции: markdown и переносы
/// строк она читает вслух как «звёздочка звёздочка».
/// </remarks>
public static partial class KotaePrompt
{
    /// <summary>Предел длины ответа для озвучивания: длиннее станция не прочитает.</summary>
    public const int MaxAnswerChars = 480;

    /// <summary>
    /// Системное сообщение: базовая подсказка плюс текущая дата и пояс.
    /// </summary>
    /// <param name="basePrompt">Подсказка из настроек.</param>
    /// <param name="now">Текущий момент.</param>
    /// <param name="zone">Часовой пояс пользователя.</param>
    /// <returns>Готовый текст системного сообщения.</returns>
    public static string BuildSystem(string basePrompt, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var date = local.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);

        return $"{basePrompt.TrimEnd()} Сейчас {date}, {time} (пояс {zone.Id}). " +
               "Если спрашивают про сегодняшний день, дату или время — используй именно эти значения.";
    }

    /// <summary>
    /// Убирает markdown и лишние пробелы, обрезает по границе предложения.
    /// </summary>
    /// <param name="answer">Сырой текст модели.</param>
    /// <param name="maxChars">Предел длины.</param>
    /// <returns>Текст, который не стыдно отдать в синтез речи.</returns>
    public static string Clean(string? answer, int maxChars = MaxAnswerChars)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return string.Empty;
        }

        var text = MarkdownLinks().Replace(answer, "$1");
        text = Emphasis().Replace(text, string.Empty);
        text = Headings().Replace(text, string.Empty);
        text = Spaces().Replace(text, " ");
        text = text.Trim();

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = text.LastIndexOfAny(['.', '!', '?'], Math.Min(maxChars, text.Length - 1));
        return cut > 40 ? text[..(cut + 1)] : text[..maxChars].TrimEnd() + "…";
    }

    /// <summary>
    /// Очищает и форматирует пару «предыдущий вопрос — предыдущий ответ» для истории диалога.
    /// Возвращает null, если контекст пустой.
    /// </summary>
    /// <param name="context">Контекст предыдущего хода.</param>
    /// <param name="maxChars">Предел длины ответа.</param>
    public static (string Question, string Answer)? CleanHistory(AnswerContext? context, int maxChars = MaxAnswerChars)
    {
        if (context is null || !context.HasContext)
        {
            return null;
        }

        string? q = context.PreviousQuestion?.Trim();
        string? a = Clean(context.PreviousAnswer, maxChars);

        if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(a))
        {
            return null;
        }

        return (q, a);
    }

    /// <summary>
    /// Собирает текст предыдущего контекста в виде «Предыдущий вопрос: … / Предыдущий ответ: …».
    /// </summary>
    /// <param name="context">Контекст предыдущего хода.</param>
    /// <param name="maxChars">Предел длины ответа.</param>
    public static string? FormatHistoryText(AnswerContext? context, int maxChars = MaxAnswerChars)
    {
        var cleaned = CleanHistory(context, maxChars);
        if (cleaned is null)
        {
            return null;
        }

        return $"Предыдущий вопрос: {cleaned.Value.Question}\nПредыдущий ответ: {cleaned.Value.Answer}";
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLinks();

    [GeneratedRegex(@"\*\*|__|`+|(?<!\w)\*|\*(?!\w)")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*")]
    private static partial Regex Headings();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
