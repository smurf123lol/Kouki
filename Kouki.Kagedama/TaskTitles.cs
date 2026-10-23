namespace Kouki.Kagedama;

/// <summary>
/// Правила заголовка задачи: одна фраза — один заголовок, длинные обрезаются.
/// </summary>
/// <remarks>
/// Здесь намеренно нет разбора фразы. «Запиши задачу купить хлеб» — это рамка императива,
/// и отделять её должен слой распознавания, где шаблон известен (см. <c>IntentRule</c> и слот <c>body</c>).
/// Попытка отрезать вводные слова постфактум по списку ломает нормальные фразы:
/// «Задача проекта — сдать отчёт» потеряла бы начало.
/// </remarks>
public static class TaskTitles
{
    /// <summary>Сколько символов заголовка максимум; длиннее — обрезается с многоточием.</summary>
    public const int Limit = 80;

    /// <summary>
    /// Делает заголовок из фразы: чистит края, снимает конечную точку и обрезает по длине.
    /// </summary>
    /// <param name="text">Фраза целиком или её часть без императива.</param>
    /// <returns>Заголовок задачи.</returns>
    public static string Make(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string trimmed = text.Trim().TrimEnd('.', '!', '?', ';', ',', ':', ' ');
        return trimmed.Length <= Limit ? trimmed : trimmed[..Limit].TrimEnd() + "…";
    }
}
