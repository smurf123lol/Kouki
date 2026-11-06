using System.Globalization;
using Kouki.Mugura.Registry;

namespace Kouki.IntentDataset;

/// <summary>
/// CLI сборщика датасета. Разбор аргументов и <see cref="Run"/> отделены от <c>Main</c>,
/// чтобы тесты могли позвать сборщик без запуска процесса.
/// </summary>
public static class IntentDatasetCli
{
    /// <summary>Код выхода: всё хорошо.</summary>
    public const int ExitSuccess = 0;

    /// <summary>Код выхода: проверка не пройдена (для <c>--check</c>).</summary>
    public const int ExitCheckFailed = 1;

    /// <summary>Код выхода: собрать нельзя (битый вход, неизвестный аргумент).</summary>
    public const int ExitInvalid = 2;

    /// <summary>Запускает сборщик; возвращает код выхода процесса.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="stdout">Куда печатать результат.</param>
    /// <param name="stderr">Куда печатать ошибки и предупреждения.</param>
    /// <param name="phraseBaseDirectory">Каталог для путей <c>PhrasesFile</c>; по умолчанию каталог приложения.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? phraseBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        DatasetOptions options;
        try
        {
            if (!TryParse(args, stdout, out options))
            {
                return ExitSuccess;
            }
        }
        catch (DatasetException exception)
        {
            stderr.WriteLine(exception.Message);
            return ExitInvalid;
        }

        try
        {
            if (phraseBaseDirectory is not null)
            {
                options = new DatasetOptions
                {
                    OutPath = options.OutPath,
                    DatabasePath = options.DatabasePath,
                    JournalWeight = options.JournalWeight,
                    CheckOnly = options.CheckOnly,
                    Stats = options.Stats,
                    ClassesFile = options.ClassesFile,
                    ChatFile = options.ChatFile,
                    PhraseBaseDirectory = phraseBaseDirectory,
                };
            }

            var registry = new CommandRegistry(new[] { typeof(Kouki.Kagedama.Commands.AddTaskHandler).Assembly });
            DatasetBuild build = DatasetAssembler.Build(options, registry, stderr);

            if (options.CheckOnly)
            {
                return ReportCheck(build, stdout, stderr);
            }

            if (options.Stats)
            {
                PrintStats(build, stdout);
            }

            if (string.IsNullOrWhiteSpace(options.OutPath))
            {
                // --stats без --out — это отчёт, а не сборка: счётчики уже напечатаны.
                if (options.Stats)
                {
                    return ExitSuccess;
                }

                throw new DatasetException("Не задан путь вывода: --out <path>.");
            }

            DatasetWriter.Write(options.OutPath, build.Records);
            stdout.WriteLine($"Датасет записан: {options.OutPath} — {build.Records.Count} записей, {build.Classes.Count} классов.");
            return ExitSuccess;
        }
        catch (DatasetException exception)
        {
            stderr.WriteLine(exception.Message);
            return ExitInvalid;
        }
        catch (IOException exception)
        {
            stderr.WriteLine($"Не удалось записать датасет: {exception.Message}");
            return ExitInvalid;
        }
    }

    private static bool TryParse(string[] args, TextWriter stdout, out DatasetOptions options)
    {
        string? outPath = null;
        string? databasePath = null;
        string? classesFile = null;
        string? chatFile = null;
        double journalWeight = DatasetDefaults.JournalWeight;
        bool checkOnly = false;
        bool stats = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--help":
                case "-h":
                    PrintUsage(stdout);
                    options = new DatasetOptions();
                    return false;
                case "--check":
                    checkOnly = true;
                    break;
                case "--stats":
                    stats = true;
                    break;
                case "--out":
                    outPath = ReadValue(args, ref index);
                    break;
                case "--db":
                    databasePath = ReadValue(args, ref index);
                    break;
                case "--classes-file":
                    classesFile = ReadValue(args, ref index);
                    break;
                case "--chat-file":
                    chatFile = ReadValue(args, ref index);
                    break;
                case "--journal-weight":
                    string rawWeight = ReadValue(args, ref index);
                    if (!double.TryParse(rawWeight, NumberStyles.Float, CultureInfo.InvariantCulture, out journalWeight) || journalWeight <= 0d)
                    {
                        throw new DatasetException($"Аргумент '--journal-weight' ожидает положительное число, получено '{rawWeight}'.");
                    }

                    break;
                default:
                    throw new DatasetException($"Неизвестный аргумент '{argument}'. Смотрите --help.");
            }
        }

        if (args.Length == 0)
        {
            PrintUsage(stdout);
            throw new DatasetException("Не задан путь вывода: --out <path>.");
        }

        options = new DatasetOptions
        {
            OutPath = outPath,
            DatabasePath = databasePath,
            JournalWeight = journalWeight,
            CheckOnly = checkOnly,
            Stats = stats,
            ClassesFile = classesFile ?? DatasetDefaults.ClassesFile(),
            ChatFile = chatFile ?? DatasetDefaults.ChatFile(),
            PhraseBaseDirectory = DatasetDefaults.PhraseBaseDirectory,
        };
        return true;
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new DatasetException($"Аргумент '{args[index]}' требует значения.");
        }

        index++;
        return args[index];
    }

    private static int ReportCheck(DatasetBuild build, TextWriter stdout, TextWriter stderr)
    {
        if (build.Records.Count == 0)
        {
            stderr.WriteLine("Проверка не пройдена: датасет пуст.");
            return ExitCheckFailed;
        }

        string[] emptyClasses = build.Classes
            .Where(label => build.RecordsByClass[label] == 0)
            .ToArray();
        if (emptyClasses.Length > 0)
        {
            stderr.WriteLine($"Проверка не пройдена: нет фраз у классов: {string.Join(", ", emptyClasses)}.");
            return ExitCheckFailed;
        }

        stdout.WriteLine($"Проверка пройдена: {build.Classes.Count} классов, {build.Records.Count} записей, {build.CommandPhraseCount} фраз команд, {build.JournalEntryCount} записей журнала.");
        return ExitSuccess;
    }

    private static void PrintStats(DatasetBuild build, TextWriter stdout)
    {
        const int classWidth = 20;
        string header = "Класс".PadRight(classWidth) + "команд" + "  " + "журнал" + "  " + "записей" + "  " + "вес";
        stdout.WriteLine(header);

        double totalWeight = 0d;
        foreach (string label in build.Classes)
        {
            IReadOnlyList<DatasetRecord> classRecords = build.Records.Where(record => record.Label == label).ToArray();
            double weight = classRecords.Sum(static record => record.Weight);
            totalWeight += weight;

            int commandCount = build.CommandPhrasesByClass.TryGetValue(label, out IReadOnlyList<string>? phrases) ? phrases.Count : 0;
            int journalCount = build.JournalByClass.GetValueOrDefault(label);

            stdout.WriteLine(
                label.PadRight(classWidth)
                + commandCount.ToString(CultureInfo.InvariantCulture).PadRight(8)
                + journalCount.ToString(CultureInfo.InvariantCulture).PadRight(8)
                + classRecords.Count.ToString(CultureInfo.InvariantCulture).PadRight(9)
                + weight.ToString("0.#", CultureInfo.InvariantCulture));
        }

        stdout.WriteLine($"Итого: {build.Classes.Count} классов, {build.CommandPhraseCount} фраз команд, {build.JournalEntryCount} записей журнала, {build.Records.Count} записей, вес {totalWeight.ToString("0.#", CultureInfo.InvariantCulture)}.");
        if (build.DuplicateTextsAcrossClasses.Count > 0)
        {
            stdout.WriteLine($"Дубли текстов между классами: {build.DuplicateTextsAcrossClasses.Count}.");
            foreach (DuplicateText duplicate in build.DuplicateTextsAcrossClasses.Where(static duplicate => !duplicate.HasJournalSource).Take(10))
            {
                stdout.WriteLine($"  {duplicate.Text}");
            }

            // Тексты из журнала — живые фразы человека: печатаем только счётчик и классы.
            DuplicateText[] fromJournal = build.DuplicateTextsAcrossClasses
                .Where(static duplicate => duplicate.HasJournalSource)
                .ToArray();
            if (fromJournal.Length > 0)
            {
                string journalClasses = string.Join(
                    ", ",
                    fromJournal.SelectMany(static duplicate => duplicate.Labels).Distinct(StringComparer.Ordinal).OrderBy(static label => label, StringComparer.Ordinal));
                stdout.WriteLine($"  из журнала: {fromJournal.Length} — тексты не печатаются (классы: {journalClasses}).");
            }
        }
        else
        {
            stdout.WriteLine("Дубли текстов между классами: 0.");
        }
    }

    private static void PrintUsage(TextWriter stdout)
    {
        stdout.WriteLine("Сборка датасета интентов из файлов фраз команд и журнала разбора.");
        stdout.WriteLine();
        stdout.WriteLine("Использование:");
        stdout.WriteLine("  Kouki.IntentDataset --out <path> [--db <path>] [--journal-weight <n>] [--stats]");
        stdout.WriteLine("  Kouki.IntentDataset --stats [--db <path>] — только счётчики, без записи");
        stdout.WriteLine("  Kouki.IntentDataset --check [--db <path>]");
        stdout.WriteLine();
        stdout.WriteLine("Аргументы:");
        stdout.WriteLine("  --out <path>            куда писать JSONL (обязательно, если не --check и не --stats)");
        stdout.WriteLine("  --db <path>             SQLite с журналом classifications; без него — только файлы");
        stdout.WriteLine("  --journal-weight <n>    вес записи журнала, по умолчанию 3");
        stdout.WriteLine("  --check                 только проверить вход и вернуть код 0/1");
        stdout.WriteLine("  --stats                 напечатать таблицу по классам");
        stdout.WriteLine("  --classes-file <path>   список классов, по умолчанию datasets/intents/classes.json");
        stdout.WriteLine("  --chat-file <path>      фразы класса chat, по умолчанию datasets/intents/chat.txt");
        stdout.WriteLine("  --help                  эта справка");
    }
}
