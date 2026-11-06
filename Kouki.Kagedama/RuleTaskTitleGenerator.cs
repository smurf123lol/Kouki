using System.Text.RegularExpressions;

namespace Kouki.Kagedama;

/// <summary>
/// Быстрый генератор заголовка на правилах (регулярных выражениях):
/// отсекает рамки императива («запиши задачу», «надо», «пожалуйста», «сохрани таск»)
/// и форматирует чистый заголовок с заглавной буквы.
/// </summary>
public sealed class RuleTaskTitleGenerator : ITaskTitleGenerator
{
    public static readonly RuleTaskTitleGenerator Instance = new();

    private static readonly Regex PrefixRegex = new(
        @"^\s*(?:(?:пожалуйста|плиз|коуки|алиса)[,\s]+)*" +
        @"(?:(?:запиши|записать|добавь|сохрани|поставь|создай|зафиксируй|сделай)\s*(?:мне\s*)?(?:пожалуйста\s*)?(?:задач\w*|таск\w*|дел\w*|в\s+задачи|в\s+дела|в\s+таски)?|" +
        @"(?:задач\w*|таск\w*|дел\w*)\s*(?:на\s*)?|" +
        @"(?:надо|нужно|не\s+забыть)(?:\s+бы)?)\s*[:,-]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public Task<string> GenerateTitleAsync(string rawText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return Task.FromResult(string.Empty);
        }

        string trimmed = rawText.Trim();
        string stripped = PrefixRegex.Replace(trimmed, string.Empty).TrimStart(':', ',', '-', ' ');
        bool strippedPrefix = stripped.Length < trimmed.Length;

        // Если после отсечения ничего не осталось или остался один мусор — берём исходный текст
        string titleCandidate = string.IsNullOrWhiteSpace(stripped) ? trimmed : stripped;

        // Приводим первую букву к заглавной только если префикс действительно был отсечен
        if (strippedPrefix && titleCandidate.Length > 0 && char.IsLower(titleCandidate[0]))
        {
            titleCandidate = char.ToUpper(titleCandidate[0]) + titleCandidate[1..];
        }

        return Task.FromResult(TaskTitles.Make(titleCandidate));
    }
}
