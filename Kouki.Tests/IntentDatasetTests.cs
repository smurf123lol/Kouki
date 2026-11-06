using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kouki.IntentDataset;
using Microsoft.Data.Sqlite;

namespace Kouki.Tests;

/// <summary>
/// Сборщик датасета T80: фразы команд лежат файлами, датасет собирается из них и журнала,
/// ошибки входа — с точными текстами из спецификации.
/// </summary>
public sealed class IntentDatasetTests
{
    private static readonly string[] ExpectedClasses =
    [
        "add_task",
        "list_tasks",
        "complete_task",
        "add_reminder",
        "list_reminders",
        "complete_reminder",
        "ask",
        "chat",
    ];

    private static readonly Dictionary<string, int> ExpectedPhraseCounts = new(StringComparer.Ordinal)
    {
        ["add_task"] = 45,
        ["list_tasks"] = 30,
        ["complete_task"] = 26,
        ["add_reminder"] = 20,
        ["list_reminders"] = 28,
        ["complete_reminder"] = 25,
        ["ask"] = 30,
        ["chat"] = 30,
    };

    [Fact]
    public void Phrase_files_and_class_files_are_in_build_output()
    {
        string output = AppContext.BaseDirectory;

        foreach (string command in ExpectedPhraseCounts.Keys.Where(static label => label != "chat"))
        {
            string path = Path.Combine(output, "Phrases", command + ".txt");
            Assert.True(File.Exists(path), $"Нет файла фраз в выводе сборки: {path}");
            Assert.NotEmpty(File.ReadAllLines(path).Where(static line => line.Length > 0 && !line.TrimStart().StartsWith('#')));
        }

        Assert.True(File.Exists(Path.Combine(output, "datasets", "intents", "classes.json")));
        Assert.True(File.Exists(Path.Combine(output, "datasets", "intents", "chat.txt")));
    }

    [Fact]
    public void Classes_file_matches_the_model_contract()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(DatasetDefaults.ClassesFile()));
        string[] classes = document.RootElement.GetProperty("classes").EnumerateArray()
            .Select(static element => element.GetString()!)
            .ToArray();

        Assert.Equal(ExpectedClasses, classes);
    }

    [Fact]
    public void Build_without_journal_produces_234_records_in_class_order()
    {
        using var temp = new TempDirectory();
        string output = Path.Combine(temp.Path, "dataset.jsonl");

        CliResult result = Run("--out", output);

        Assert.Equal(IntentDatasetCli.ExitSuccess, result.Code);
        Assert.Empty(result.Error);

        DatasetRecord[] records = ReadRecords(output);
        Assert.Equal(234, records.Length);
        Assert.All(records, static record => Assert.Equal("command", record.Source));
        Assert.All(records, static record => Assert.Equal(1d, record.Weight));

        foreach ((string label, int expected) in ExpectedPhraseCounts)
        {
            Assert.Equal(expected, records.Count(record => record.Label == label));
        }

        // Классы идут в порядке classes.json, внутри класса — в порядке файла фраз.
        int[] classIndexes = records.Select(record => Array.IndexOf(ExpectedClasses, record.Label)).ToArray();
        Assert.Equal(classIndexes.OrderBy(static index => index), classIndexes);

        string[] addTaskPhrases = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Phrases", "add_task.txt"))
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        Assert.Equal(addTaskPhrases, records.Where(static record => record.Label == "add_task").Select(static record => record.Text));
    }

    [Fact]
    public void Check_passes_on_repository_files()
    {
        CliResult result = Run("--check");

        Assert.Equal(IntentDatasetCli.ExitSuccess, result.Code);
        Assert.Contains("Проверка пройдена", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Two_runs_produce_identical_bytes()
    {
        using var temp = new TempDirectory();
        string first = Path.Combine(temp.Path, "first.jsonl");
        string second = Path.Combine(temp.Path, "second.jsonl");

        Assert.Equal(IntentDatasetCli.ExitSuccess, Run("--out", first).Code);
        Assert.Equal(IntentDatasetCli.ExitSuccess, Run("--out", second).Code);

        Assert.Equal(Sha256(first), Sha256(second));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Missing_phrase_file_fails_with_exact_message()
    {
        using var temp = new TempDirectory();
        string phrasesRoot = CopyPhrasesTo(temp.Path);
        File.Delete(Path.Combine(phrasesRoot, "Phrases", "add_task.txt"));

        CliResult result = RunWithPhraseRoot(phrasesRoot, "--out", Path.Combine(temp.Path, "dataset.jsonl"));

        Assert.Equal(IntentDatasetCli.ExitInvalid, result.Code);
        Assert.Equal(
            $"Файл фраз команды 'add_task': 'Phrases/add_task.txt' не найден (ожидался в '{Path.Combine(phrasesRoot, "Phrases", "add_task.txt")}').",
            result.Error.Trim());
    }

    [Fact]
    public void Long_phrase_fails_with_exact_message()
    {
        using var temp = new TempDirectory();
        string phrasesRoot = CopyPhrasesTo(temp.Path);
        File.WriteAllText(Path.Combine(phrasesRoot, "Phrases", "add_task.txt"), new string('я', 201));

        CliResult result = RunWithPhraseRoot(phrasesRoot, "--out", Path.Combine(temp.Path, "dataset.jsonl"));

        Assert.Equal(IntentDatasetCli.ExitInvalid, result.Code);
        Assert.Equal("Файл фраз команды 'add_task': строка 1: фраза длиннее 200 символов.", result.Error.Trim());
    }

    [Fact]
    public void Label_outside_classes_fails_with_exact_message()
    {
        using var temp = new TempDirectory();
        string classesFile = Path.Combine(temp.Path, "classes.json");
        string[] withoutChat = ExpectedClasses.Where(static label => label != "chat").ToArray();
        File.WriteAllText(classesFile, JsonSerializer.Serialize(new { classes = withoutChat }));

        CliResult result = Run("--check", "--classes-file", classesFile);

        Assert.Equal(IntentDatasetCli.ExitInvalid, result.Code);
        Assert.Equal("Метка 'chat' из журнала/файла не входит в классы модели.", result.Error.Trim());
    }

    [Fact]
    public void Check_fails_when_a_class_has_no_phrases()
    {
        using var temp = new TempDirectory();
        string classesFile = Path.Combine(temp.Path, "classes.json");
        string[] withGhost = [.. ExpectedClasses, "ghost"];
        File.WriteAllText(classesFile, JsonSerializer.Serialize(new { classes = withGhost }));

        CliResult result = Run("--check", "--classes-file", classesFile);

        Assert.Equal(IntentDatasetCli.ExitCheckFailed, result.Code);
        Assert.Contains("Проверка не пройдена: нет фраз у классов: ghost.", result.Error);
    }

    [Fact]
    public void Journal_entries_add_weight_and_label_wins_over_intent()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "kouki.sqlite");
        string phraseFromFile = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Phrases", "add_task.txt"))
            .Select(static line => line.Trim())
            .First(static line => line.Length > 0 && !line.StartsWith('#'));
        CreateJournal(database,
            (inputText: phraseFromFile, intent: "add_task", label: (string?)null),
            (inputText: "купить молоко", intent: "add_task", label: (string?)null),
            (inputText: "ну что там у меня по задачам", intent: "ask", label: (string?)"list_tasks"));

        string output = Path.Combine(temp.Path, "dataset.jsonl");
        CliResult result = Run("--out", output, "--db", database);

        Assert.Equal(IntentDatasetCli.ExitSuccess, result.Code);
        Assert.Empty(result.Error);

        DatasetRecord[] records = ReadRecords(output);
        Assert.Equal(234 + 2, records.Length);

        DatasetRecord merged = Assert.Single(records, record => record.Text == phraseFromFile);
        Assert.Equal("add_task", merged.Label);
        Assert.Equal("command", merged.Source);
        Assert.Equal(4d, merged.Weight);

        DatasetRecord journal = Assert.Single(records, record => record.Text == "купить молоко");
        Assert.Equal("add_task", journal.Label);
        Assert.Equal("journal", journal.Source);
        Assert.Equal(3d, journal.Weight);

        // label сильнее intent: фраза попала в list_tasks, хотя классификатор сказал ask.
        DatasetRecord labeled = Assert.Single(records, record => record.Text == "ну что там у меня по задачам");
        Assert.Equal("list_tasks", labeled.Label);
        Assert.Equal("journal", labeled.Source);
    }

    [Fact]
    public void Stats_prints_every_class()
    {
        CliResult result = Run("--stats", "--out", Path.Combine(Path.GetTempPath(), "kouki-t80-stats-" + Guid.NewGuid().ToString("N") + ".jsonl"));

        Assert.Equal(IntentDatasetCli.ExitSuccess, result.Code);
        foreach (string label in ExpectedClasses)
        {
            Assert.Contains(label, result.Output);
        }

        Assert.Contains("Итого: 8 классов", result.Output);
    }

    [Fact]
    public void Stats_without_out_succeeds_and_does_not_write()
    {
        CliResult result = Run("--stats");

        Assert.Equal(IntentDatasetCli.ExitSuccess, result.Code);
        Assert.Empty(result.Error);
        Assert.Contains("Итого: 8 классов", result.Output);
        Assert.DoesNotContain("Датасет записан", result.Output);
    }

    [Fact]
    public void Phrase_file_with_bom_fails_with_exact_message()
    {
        using var temp = new TempDirectory();
        string phrasesRoot = CopyPhrasesTo(temp.Path);
        string path = Path.Combine(phrasesRoot, "Phrases", "add_task.txt");
        byte[] body = Encoding.UTF8.GetBytes("купить хлеб\n");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. body]);

        CliResult result = RunWithPhraseRoot(phrasesRoot, "--out", Path.Combine(temp.Path, "dataset.jsonl"));

        Assert.Equal(IntentDatasetCli.ExitInvalid, result.Code);
        Assert.Equal("Файл фраз команды 'add_task': файл должен быть в UTF-8 без BOM.", result.Error.Trim());
    }

    [Fact]
    public void Phrase_file_with_invalid_utf8_fails_with_exact_message()
    {
        using var temp = new TempDirectory();
        string phrasesRoot = CopyPhrasesTo(temp.Path);
        string path = Path.Combine(phrasesRoot, "Phrases", "add_task.txt");
        File.WriteAllBytes(path, [0xD0, 0xE8, 0xE2, (byte)'\n']);

        CliResult result = RunWithPhraseRoot(phrasesRoot, "--out", Path.Combine(temp.Path, "dataset.jsonl"));

        Assert.Equal(IntentDatasetCli.ExitInvalid, result.Code);
        Assert.Equal("Файл фраз команды 'add_task': файл не является корректным UTF-8.", result.Error.Trim());
    }

    [Fact]
    public void Stats_hides_duplicate_texts_from_journal()
    {
        using var temp = new TempDirectory();
        string database = Path.Combine(temp.Path, "kouki.sqlite");
        string journalPhrase = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Phrases", "list_tasks.txt"))
            .Select(static line => line.Trim())
            .First(static line => line.Length > 0 && !line.StartsWith('#'));
        CreateJournal(database, (inputText: journalPhrase, intent: "list_tasks", label: (string?)"add_task"));

        CliResult result = Run("--stats", "--db", database);

        Assert.Equal(IntentDatasetCli.ExitSuccess, result.Code);
        Assert.Contains("Дубли текстов между классами: 1.", result.Output);
        Assert.Contains("из журнала: 1 — тексты не печатаются", result.Output);
        Assert.DoesNotContain(journalPhrase, result.Output);
    }

    private static CliResult Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = IntentDatasetCli.Run(args, output, error);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    private static CliResult RunWithPhraseRoot(string phraseBaseDirectory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = IntentDatasetCli.Run(args, output, error, phraseBaseDirectory);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    private static string CopyPhrasesTo(string temporaryRoot)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Phrases");
        string target = Path.Combine(temporaryRoot, "Phrases");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*.txt"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return temporaryRoot;
    }

    private static DatasetRecord[] ReadRecords(string path)
    {
        return File.ReadAllLines(path)
            .Where(static line => line.Length > 0)
            .Select(static line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                return new DatasetRecord(
                    root.GetProperty("text").GetString()!,
                    root.GetProperty("label").GetString()!,
                    root.GetProperty("source").GetString()!,
                    root.GetProperty("weight").GetDouble());
            })
            .ToArray();
    }

    private static string Sha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void CreateJournal(string path, params (string inputText, string? intent, string? label)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE classifications (
                id TEXT PRIMARY KEY,
                input_text TEXT,
                intent TEXT,
                label TEXT
            );
            """;
        create.ExecuteNonQuery();

        for (int index = 0; index < rows.Length; index++)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO classifications (id, input_text, intent, label) VALUES ($id, $text, $intent, $label)";
            insert.Parameters.AddWithValue("$id", index.ToString("D4"));
            insert.Parameters.AddWithValue("$text", rows[index].inputText);
            insert.Parameters.AddWithValue("$intent", (object?)rows[index].intent ?? DBNull.Value);
            insert.Parameters.AddWithValue("$label", (object?)rows[index].label ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    private sealed record CliResult(int Code, string Output, string Error);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kouki-t80-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // временный каталог остаётся в _scratch-подобном месте ОС; тест не должен падать из-за уборки
            }
        }
    }
}
