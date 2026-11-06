namespace Kouki.IntentDataset;

/// <summary>
/// Читает файл фраз: UTF-8 без BOM, одна фраза в строке, строки с <c>#</c> — комментарии,
/// пустые строки игнорируются. Формат описан в спецификации T80 §2.
/// </summary>
public static class PhraseFileReader
{
    /// <summary>Минимальная длина фразы в символах.</summary>
    public const int MinPhraseLength = 2;

    /// <summary>Максимальная длина фразы в символах.</summary>
    public const int MaxPhraseLength = 200;

    /// <summary>Маркер строки-комментария.</summary>
    public const char CommentMarker = '#';

    /// <summary>
    /// Читает фразы из файла и проверяет формат. Любая поломка — <see cref="DatasetException"/>
    /// с точным текстом из спецификации.
    /// </summary>
    /// <param name="resolvedPath">Полный путь к файлу на диске.</param>
    /// <param name="displayPath">Путь, который показываем в ошибке (как объявлен в атрибуте).</param>
    /// <param name="owner">Чей это файл, например «Файл фраз команды 'add_task'».</param>
    public static IReadOnlyList<string> Read(string resolvedPath, string displayPath, string owner)
    {
        if (!File.Exists(resolvedPath))
        {
            throw new DatasetException($"{owner}: '{displayPath}' не найден (ожидался в '{resolvedPath}').");
        }

        string[] lines = File.ReadAllLines(resolvedPath);
        var phrases = new List<string>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            string trimmed = line.Trim();
            int number = index + 1;
            if (trimmed.Length == 0)
            {
                throw new DatasetException($"{owner}: строка {number}: пустая фраза после обрезки пробелов.");
            }

            if (trimmed[0] == CommentMarker)
            {
                continue;
            }

            if (trimmed.Length > MaxPhraseLength)
            {
                throw new DatasetException($"{owner}: строка {number}: фраза длиннее {MaxPhraseLength} символов.");
            }

            if (trimmed.Length < MinPhraseLength)
            {
                throw new DatasetException($"{owner}: строка {number}: фраза короче {MinPhraseLength} символов.");
            }

            phrases.Add(trimmed);
        }

        if (phrases.Count == 0)
        {
            throw new DatasetException($"{owner}: '{displayPath}' пуст.");
        }

        return phrases;
    }
}
