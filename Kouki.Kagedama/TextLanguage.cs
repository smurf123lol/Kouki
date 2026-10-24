using System.Text;

namespace Kouki.Kagedama;

/// <summary>
/// Грубая оценка языка по алфавиту: нужна не для перевода, а чтобы помечать
/// записи, которые пришли не по-русски.
/// </summary>
/// <remarks>
/// Распознавание работает с подсказкой «ru», но это не фильтр: английскую фразу
/// whisper либо переведёт, либо запишет латиницей, и в задачах появится
/// «осмысленный, но не тот» текст. Пометка в мете даёт возможность потом
/// отфильтровать такие записи, не угадывая задним числом.
/// </remarks>
public static class TextLanguage
{
    /// <summary>Метка для записи, в которой латиницы заметно больше кириллицы.</summary>
    public const string NonRussian = "non-ru";

    /// <summary>Метка для записи без букв вообще (цифры, символы).</summary>
    public const string NoLetters = "no-letters";

    /// <summary>
    /// Возвращает <see cref="NonRussian"/>, <see cref="NoLetters"/> или <c>null</c>,
    /// если текст выглядит русским.
    /// </summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return NoLetters;
        }

        var cyrillic = 0;
        var latin = 0;

        foreach (var symbol in text)
        {
            if (symbol is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё')
            {
                cyrillic++;
            }
            else if (symbol is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                latin++;
            }
        }

        if (cyrillic == 0 && latin == 0)
        {
            return NoLetters;
        }

        // Одиночная латиница бывает в названиях («GPT», «Wi-Fi») — это ещё не чужой язык.
        return latin > cyrillic * 2 && latin >= 6 ? NonRussian : null;
    }

    /// <summary>Короткая подпись для меты и журнала.</summary>
    public static string Describe(string? text, string? hint)
    {
        if (hint is null)
        {
            return "ru";
        }

        var letters = new StringBuilder();
        foreach (var symbol in text ?? string.Empty)
        {
            if (char.IsLetter(symbol) && letters.Length < 24)
            {
                letters.Append(symbol);
            }
        }

        return letters.Length == 0 ? hint : $"{hint}: {letters}";
    }
}
