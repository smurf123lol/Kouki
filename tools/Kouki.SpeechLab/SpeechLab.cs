namespace Kouki.SpeechLab;

using System.Globalization;
using System.Text;

/// <summary>
/// Небольшая лаборатория речи: синтез, распознавание и прогон «туда-обратно».
/// </summary>
/// <remarks>
/// Инструмент нужен, чтобы проверять голосовой конвейер на воспроизводимом входе,
/// не записывая микрофон: фразу синтезируем, прогоняем через тот же распознаватель,
/// что стоит в сервере, и сравниваем текст.
/// </remarks>
internal static class SpeechLab
{
    private const string DefaultAsr =
        @"models\asr\sherpa-onnx-whisper-small";

    private const string DefaultTts =
        @"models\tts\vits-piper-ru_RU-irina-medium";

    /// <summary>Точка входа.</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string root = RepositoryRoot();
        string command = args[0].ToLowerInvariant();
        Dictionary<string, string> options = ParseOptions(args.Skip(1));
        string? text = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;

        try
        {
            switch (command)
            {
                case "say":
                    return Say(root, text ?? "Запиши задачу купить хлеб", options);
                case "hear":
                    return Hear(root, text ?? options.GetValueOrDefault("wav") ?? string.Empty, options);
                case "roundtrip":
                    return RoundTrip(root, text ?? "Запиши задачу купить хлеб", options);
                case "dump":
#if KOUKI_SHERPA
                    return ApiDump.Print(args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray());
#else
                    Console.Error.WriteLine("dump доступен только в сборке с -p:KoukiEnableSherpa=true.");
                    return 1;
#endif
                case "help":
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Неизвестная команда '{command}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    private static int Say(string root, string text, Dictionary<string, string> options)
    {
        string ttsDirectory = Path.Combine(root, options.GetValueOrDefault("tts", DefaultTts));
        string output = Path.GetFullPath(
            options.GetValueOrDefault("out", Path.Combine(Path.GetTempPath(), "kouki-say.wav")));
        float speed = float.Parse(options.GetValueOrDefault("speed", "1"), CultureInfo.InvariantCulture);

        using TtsVoice voice = TtsVoice.Load(ttsDirectory);
        IReadOnlyList<float> samples = voice.Say(text, speed);
        WavWriter.Write(output, samples);

        Console.WriteLine($"Фраза: «{text}»");
        Console.WriteLine($"Аудио: {output} ({Seconds(samples.Count):0.00} с, {WavWriter.SampleRate} Гц)");
        return 0;
    }

    private static int Hear(string root, string wavPath, Dictionary<string, string> options)
    {
        if (string.IsNullOrWhiteSpace(wavPath))
        {
            Console.Error.WriteLine("Укажите путь к WAV: hear <файл> [--asr <каталог>] [--lang ru]");
            return 1;
        }

        string asrDirectory = Path.Combine(root, options.GetValueOrDefault("asr", DefaultAsr));
        string language = options.GetValueOrDefault("lang", "ru");

        using var transcriber = new Transcriber(asrDirectory, language);
        byte[] wav = File.ReadAllBytes(Path.GetFullPath(wavPath));
        TranscriptionReport report = transcriber.Transcribe(wav);

        Console.WriteLine($"Файл: {wavPath}");
        Console.WriteLine($"Услышано: «{report.Text}»");
        return 0;
    }

    private static int RoundTrip(string root, string expected, Dictionary<string, string> options)
    {
        string ttsDirectory = Path.Combine(root, options.GetValueOrDefault("tts", DefaultTts));
        string asrDirectory = Path.Combine(root, options.GetValueOrDefault("asr", DefaultAsr));
        string language = options.GetValueOrDefault("lang", "ru");
        string output = Path.GetFullPath(
            options.GetValueOrDefault("out", Path.Combine(Path.GetTempPath(), "kouki-roundtrip.wav")));

        using var voice = TtsVoice.Load(ttsDirectory);
        IReadOnlyList<float> samples = voice.Say(expected);
        WavWriter.Write(output, samples);

        using var transcriber = new Transcriber(asrDirectory, language);
        TranscriptionReport report = transcriber.Transcribe(File.ReadAllBytes(output));

        double similarity = Similarity(expected, report.Text);

        Console.WriteLine($"Сказали: «{expected}»");
        Console.WriteLine($"Услышали: «{report.Text}»");
        Console.WriteLine($"Совпадение: {similarity:P0}");
        Console.WriteLine($"Аудио: {output} ({Seconds(samples.Count):0.00} с)");

        if (similarity < 0.7)
        {
            Console.WriteLine("ВНИМАНИЕ: текст сильно расходится — смотрите модель и язык.");
            return 2;
        }

        return 0;
    }

    /// <summary>Проверяет, что фразу можно «услышать» текстом, без участия человека.</summary>
    private static double Seconds(int samples) => (double)samples / WavWriter.SampleRate;

    /// <summary>Сравнение по нормализованному расстоянию Левенштейна.</summary>
    private static double Similarity(string expected, string actual)
    {
        string left = Normalize(expected);
        string right = Normalize(actual);

        if (left.Length == 0 && right.Length == 0)
        {
            return 1;
        }

        int distance = Levenshtein(left, right);
        return 1 - ((double)distance / Math.Max(left.Length, right.Length));
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (char symbol in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(symbol))
            {
                builder.Append(symbol);
            }
            else if (char.IsWhiteSpace(symbol))
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int Levenshtein(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] list = args.ToArray();

        for (int i = 0; i < list.Length; i++)
        {
            string arg = list[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            int separator = arg.IndexOf('=');

            if (separator >= 0)
            {
                options[arg[2..separator]] = arg[(separator + 1)..];
                continue;
            }

            // Поддерживаем оба вида: --out=файл и --out файл.
            if (i + 1 < list.Length && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[arg[2..]] = list[++i];
                continue;
            }

            options[arg[2..]] = "true";
        }

        return options;
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Kouki.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Kouki.SpeechLab — проверка речи без микрофона.");
        Console.WriteLine();
        Console.WriteLine("  say <фраза> [--out файл.wav] [--tts каталог] [--speed 1.0]");
        Console.WriteLine("      синтезировать русскую речь в WAV 16 кГц моно");
        Console.WriteLine("  hear <файл.wav> [--asr каталог] [--lang ru]");
        Console.WriteLine("      распознать WAV тем же распознавателем, что в сервере");
        Console.WriteLine("  roundtrip <фраза> [--asr каталог] [--tts каталог] [--lang ru]");
        Console.WriteLine("      синтез → распознавание → сравнение с исходной фразой");
        Console.WriteLine();
        Console.WriteLine($"По умолчанию: ASR {DefaultAsr}, TTS {DefaultTts}.");
        Console.WriteLine("Требуется сборка с -p:KoukiEnableSherpa=true.");
    }
}
