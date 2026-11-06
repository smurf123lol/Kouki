using System.Text;

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

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

        // Спека §2: файл строго в UTF-8 без BOM. File.ReadAllLines молча снимает BOM
        // и подставляет U+FFFD на cp1251, поэтому декодируем сами и падаем fail-closed.
        byte[] bytes = File.ReadAllBytes(resolvedPath);
        if (HasUtf8Bom(bytes))
        {
            throw new DatasetException($"{owner}: файл должен быть в UTF-8 без BOM.");
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new DatasetException($"{owner}: файл не является корректным UTF-8.");
        }

        string[] lines = content.Split('\n');
        var phrases = new List<string>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
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

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }
}
