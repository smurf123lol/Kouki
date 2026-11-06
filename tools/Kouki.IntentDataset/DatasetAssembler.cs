using System.Text.Json;
using Kouki.Mugura.Registry;
using Microsoft.Data.Sqlite;

namespace Kouki.IntentDataset;

/// <summary>
/// Собирает датасет: фразы команд из файлов + записи журнала разбора. Порядок
/// детерминированный — классы как в classes.json, внутри класса сначала фразы команды
/// в порядке файла, затем журнал по возрастанию id.
/// </summary>
public static class DatasetAssembler
{
    /// <summary>Метка класса, который не является командой.</summary>
    public const string ChatLabel = "chat";

    /// <summary>Вес одной фразы команды: каждая строка файла весит единицу.</summary>
    public const double CommandPhraseWeight = 1d;

    private static readonly JsonSerializerOptions ClassesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Собирает датасет или бросает <see cref="DatasetException"/> с точным текстом.</summary>
    public static DatasetBuild Build(DatasetOptions options, CommandRegistry registry, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IReadOnlyList<string> classes = ReadClasses(options.ClassesFile);

        var commandPhrases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (CommandEntry entry in OrderedEntries(registry))
        {
            if (entry.PhrasesFile is null)
            {
                continue;
            }

            string resolvedPath = Path.Combine(options.PhraseBaseDirectory, entry.PhrasesFile);
            IReadOnlyList<string> phrases = PhraseFileReader.Read(
                resolvedPath,
                entry.PhrasesFile,
                $"Файл фраз команды '{entry.Name}'");
            EnsureKnownLabel(entry.Name, classes);
            commandPhrases[entry.Name] = phrases;
        }

        IReadOnlyList<string> chatPhrases = PhraseFileReader.Read(
            Path.GetFullPath(options.ChatFile),
            options.ChatFile,
            $"Файл фраз класса '{ChatLabel}'");
        EnsureKnownLabel(ChatLabel, classes);
        commandPhrases[ChatLabel] = chatPhrases;

        IReadOnlyList<JournalEntry>? journal = ReadJournal(options.DatabasePath, classes, diagnostics);

        var records = new List<DatasetRecord>();
        var positions = new Dictionary<(string Text, string Label), int>();
        var commandByClass = new Dictionary<string, int>(StringComparer.Ordinal);
        var journalByClass = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string text, string label, string source, double weight)
        {
            if (positions.TryGetValue((text, label), out int position))
            {
                DatasetRecord existing = records[position];
                records[position] = existing with { Weight = existing.Weight + weight };
                return;
            }

            positions[(text, label)] = records.Count;
            records.Add(new DatasetRecord(text, label, source, weight));
        }

        foreach (string label in classes)
        {
            if (commandPhrases.TryGetValue(label, out IReadOnlyList<string>? phrases))
            {
                commandByClass[label] = phrases.Count;
                foreach (string phrase in phrases)
                {
                    Add(phrase, label, "command", CommandPhraseWeight);
                }
            }
            else
            {
                commandByClass[label] = 0;
            }

            int journalCount = 0;
            if (journal is not null)
            {
                foreach (JournalEntry row in journal)
                {
                    if (!string.Equals(row.Label, label, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    journalCount++;
                    Add(row.Text, label, "journal", options.JournalWeight);
                }
            }

            journalByClass[label] = journalCount;
        }

        var labelsByText = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (DatasetRecord record in records)
        {
            if (!labelsByText.TryGetValue(record.Text, out HashSet<string>? labels))
            {
                labels = new HashSet<string>(StringComparer.Ordinal);
                labelsByText[record.Text] = labels;
            }

            labels.Add(record.Label);
        }

        string[] duplicates = labelsByText
            .Where(static pair => pair.Value.Count > 1)
            .Select(static pair => pair.Key)
            .OrderBy(static text => text, StringComparer.Ordinal)
            .ToArray();

        return new DatasetBuild
        {
            Classes = classes,
            Records = records,
            CommandPhraseCount = commandByClass.Values.Sum(),
            JournalEntryCount = journal?.Count ?? 0,
            RecordsByClass = classes.ToDictionary(static label => label, label => records.Count(record => record.Label == label), StringComparer.Ordinal),
            CommandPhrasesByClass = commandPhrases,
            JournalByClass = journalByClass,
            DuplicateTextsAcrossClasses = duplicates,
        };
    }

    private static IReadOnlyList<CommandEntry> OrderedEntries(CommandRegistry registry)
    {
        return registry.Names
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(name => registry.GetEntry(name)!)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadClasses(string path)
    {
        if (!File.Exists(path))
        {
            throw new DatasetException($"Файл классов '{path}' не найден.");
        }

        ClassesFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ClassesFile>(File.ReadAllText(path), ClassesJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new DatasetException($"Файл классов '{path}' не разбирается: {exception.Message}");
        }
        catch (IOException exception)
        {
            throw new DatasetException($"Файл классов '{path}' не читается: {exception.Message}");
        }

        List<string>? classes = parsed?.Classes;
        if (classes is null || classes.Count == 0)
        {
            throw new DatasetException($"Файл классов '{path}' пуст.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in classes)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new DatasetException($"Файл классов '{path}': пустое имя класса.");
            }

            if (!seen.Add(name))
            {
                throw new DatasetException($"Файл классов '{path}': класс '{name}' повторяется.");
            }
        }

        return classes;
    }

    private static void EnsureKnownLabel(string label, IReadOnlyList<string> classes)
    {
        if (!classes.Contains(label, StringComparer.Ordinal))
        {
            throw new DatasetException($"Метка '{label}' из журнала/файла не входит в классы модели.");
        }
    }

    private static IReadOnlyList<JournalEntry>? ReadJournal(string? databasePath, IReadOnlyList<string> classes, TextWriter diagnostics)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        string dbPath = databasePath;
        if (!File.Exists(dbPath))
        {
            diagnostics.WriteLine($"Предупреждение: база '{dbPath}' не найдена — журнал пропущен, датасет собран только из файлов фраз.");
            return null;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT input_text, intent, label FROM classifications ORDER BY id";
            using SqliteDataReader reader = command.ExecuteReader();

            var rows = new List<JournalEntry>();
            while (reader.Read())
            {
                string? text = reader.IsDBNull(0) ? null : reader.GetString(0);
                string? intent = reader.IsDBNull(1) ? null : reader.GetString(1);
                string? label = reader.IsDBNull(2) ? null : reader.GetString(2);

                string? target = string.IsNullOrWhiteSpace(label) ? intent : label;
                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                string trimmed = text.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                EnsureKnownLabel(target, classes);
                rows.Add(new JournalEntry(trimmed, target));
            }

            return rows;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            diagnostics.WriteLine($"Предупреждение: база '{dbPath}' не читается ({exception.Message}) — журнал пропущен.");
            return null;
        }
    }

    private sealed class ClassesFile
    {
        public List<string>? Classes { get; set; }
    }
}
