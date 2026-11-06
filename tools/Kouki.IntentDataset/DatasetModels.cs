using Kouki.Mugura.Registry;

namespace Kouki.IntentDataset;

/// <summary>Параметры сборки датасета.</summary>
public sealed class DatasetOptions
{
    /// <summary>Куда писать JSONL. Для <see cref="CheckOnly"/> не нужен.</summary>
    public string? OutPath { get; init; }

    /// <summary>Путь к SQLite с журналом разбора. null — журнал не читаем.</summary>
    public string? DatabasePath { get; init; }

    /// <summary>Вес одной записи журнала (фразы команд весят 1).</summary>
    public double JournalWeight { get; init; } = DatasetDefaults.JournalWeight;

    /// <summary>Только проверить вход, ничего не писать.</summary>
    public bool CheckOnly { get; init; }

    /// <summary>Печатать таблицу по классам.</summary>
    public bool Stats { get; init; }

    /// <summary>Файл со списком классов модели.</summary>
    public string ClassesFile { get; init; } = DatasetDefaults.ClassesFile();

    /// <summary>Файл с фразами класса <c>chat</c> (не команда).</summary>
    public string ChatFile { get; init; } = DatasetDefaults.ChatFile();

    /// <summary>Каталог, от которого отсчитываются <c>PhrasesFile</c> команд.</summary>
    public string PhraseBaseDirectory { get; init; } = DatasetDefaults.PhraseBaseDirectory;
}

/// <summary>Значения по умолчанию, привязанные к каталогу приложения.</summary>
public static class DatasetDefaults
{
    /// <summary>Вес записи журнала по умолчанию.</summary>
    public const double JournalWeight = 3d;

    /// <summary>Путь к списку классов относительно каталога приложения.</summary>
    public static string ClassesFile() => Path.Combine(AppContext.BaseDirectory, "datasets", "intents", "classes.json");

    /// <summary>Путь к фразам чата относительно каталога приложения.</summary>
    public static string ChatFile() => Path.Combine(AppContext.BaseDirectory, "datasets", "intents", "chat.txt");

    /// <summary>Каталог приложения: от него отсчитываются пути из <c>PhrasesFile</c>.</summary>
    public static string PhraseBaseDirectory => AppContext.BaseDirectory;
}

/// <summary>Запись датасета: одна строка JSONL.</summary>
/// <param name="Text">Фраза как её сказал человек.</param>
/// <param name="Label">Класс модели.</param>
/// <param name="Source"><c>command</c> или <c>journal</c>.</param>
/// <param name="Weight">Вес записи, уже сложенный по одинаковым парам текст+метка.</param>
public sealed record DatasetRecord(string Text, string Label, string Source, double Weight);

/// <summary>Строка журнала разбора, попавшая в датасет.</summary>
/// <param name="Text">Текст фразы.</param>
/// <param name="Label">Метка: размеченная, а если её нет — интент.</param>
public sealed record JournalEntry(string Text, string Label);

/// <summary>Текст, который встречается больше чем под одной меткой.</summary>
/// <param name="Text">Текст фразы.</param>
/// <param name="Labels">Классы, в которых встречается текст, в порядке модели.</param>
/// <param name="HasJournalSource">Текст пришёл хотя бы из одной записи журнала — его нельзя печатать.</param>
public sealed record DuplicateText(string Text, IReadOnlyList<string> Labels, bool HasJournalSource);

/// <summary>Итог сборки: что прочитали и что получилось.</summary>
public sealed class DatasetBuild
{
    public required IReadOnlyList<string> Classes { get; init; }

    public required IReadOnlyList<DatasetRecord> Records { get; init; }

    /// <summary>Сколько фраз пришло из файлов команд (до схлопывания и слияния с журналом).</summary>
    public required int CommandPhraseCount { get; init; }

    /// <summary>Сколько записей пришло из журнала.</summary>
    public required int JournalEntryCount { get; init; }

    /// <summary>Итоговые записи по классам, в порядке <see cref="Classes"/>.</summary>
    public required IReadOnlyDictionary<string, int> RecordsByClass { get; init; }

    /// <summary>Фразы команд по классам, в порядке файла.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> CommandPhrasesByClass { get; init; }

    /// <summary>Сколько записей журнала по классам.</summary>
    public required IReadOnlyDictionary<string, int> JournalByClass { get; init; }

    /// <summary>Тексты, которые встречаются больше чем под одной меткой, с пометкой источника.</summary>
    public required IReadOnlyList<DuplicateText> DuplicateTextsAcrossClasses { get; init; }
}
