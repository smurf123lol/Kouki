using System.Diagnostics;
using System.Text;

namespace Kouki.Tests;

/// <summary>
/// Поведенческие проверки скрипта разметки интентов (<c>tools/intents.sh</c>).
/// </summary>
/// <remarks>
/// Скрипт живёт вне сборки, поэтому строковые проверки («есть ли POST /label») ничего
/// не гарантируют: они остаются зелёными, если логика «пропустить unclassified»
/// или «провал сервера — это провал» сломана. Здесь скрипт запускается в настоящем bash
/// с подменённым <c>curl</c>, который пишет вызовы в файл.
/// </remarks>
public sealed class IntentToolingTests
{
    [Fact]
    public void Check_accepts_a_clean_file_and_prints_the_distribution()
    {
        RunResult result = RunIntentTool(
            ["check", "--dir", "."],
            rows:
            [
                Row("a1", "запиши задачу купить хлеб", "add_task", 0.9),
                Row("a2", "покажи задачи", "list_tasks", 0.8),
                Row("a3", "запиши задачу купить хлеб", "add_task", 0.95),
            ]);

        Assert.True(result.Rc == 0, $"rc={result.Rc} out={result.Stdout} err={result.Stderr}");
        Assert.Contains("строк: 3, замечаний: 0", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("add_task", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_flags_an_unknown_intent_and_broken_json()
    {
        RunResult result = RunIntentTool(
            ["check", "--dir", "."],
            rows:
            [
                Row("a1", "фраза", "nope", 0.5),
                "это не json",
            ]);

        Assert.Equal(1, result.Rc);
        Assert.Contains("неизвестный интент 'nope'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("не JSON", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_dry_run_skips_unclassified_and_touches_nothing()
    {
        RunResult result = RunIntentTool(
            ["import", "--dir", ".", "--token", "test-token", "--dry-run"],
            rows:
            [
                Row("a1", "непонятная фраза", "unclassified", 0.2),
            ]);

        Assert.True(result.Rc == 0, $"rc={result.Rc} out={result.Stdout} err={result.Stderr}");
        Assert.Contains("пропущено 1", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("/label", result.CurlCalls, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_posts_the_label_for_every_catalog_intent()
    {
        RunResult result = RunIntentTool(
            ["import", "--dir", ".", "--token", "test-token"],
            rows:
            [
                Row("11111111-1111-1111-1111-111111111111", "запиши задачу", "add_task", 0.9),
                Row("22222222-2222-2222-2222-222222222222", "покажи задачи", "list_tasks", 0.8),
            ],
            env: new Dictionary<string, string> { ["CURL_MODE"] = "httpcode", ["CURL_CODE"] = "200" });

        Assert.True(result.Rc == 0, $"rc={result.Rc} out={result.Stdout} err={result.Stderr}");
        Assert.Contains("импортировано: 2", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("/api/classifications/11111111-1111-1111-1111-111111111111/label", result.CurlCalls, StringComparison.Ordinal);
        Assert.Contains("/api/classifications/22222222-2222-2222-2222-222222222222/label", result.CurlCalls, StringComparison.Ordinal);
        Assert.Contains("\"label\": \"add_task\"", result.CurlCalls, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_fails_when_the_server_rejects_a_label()
    {
        RunResult result = RunIntentTool(
            ["import", "--dir", ".", "--token", "test-token"],
            rows: [Row("11111111-1111-1111-1111-111111111111", "запиши задачу", "add_task", 0.9)],
            env: new Dictionary<string, string> { ["CURL_MODE"] = "httpcode", ["CURL_CODE"] = "404" });

        Assert.Equal(1, result.Rc);
        Assert.Contains("HTTP 404", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("не принято: 1", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_reports_a_divergence_with_rules_plus_llm()
    {
        RunResult result = RunIntentTool(
            ["compare", "--dir", ".", "--token", "test-token"],
            rows: [Row("a1", "сколько минут варить яйцо", "ask", 0.7)],
            env: new Dictionary<string, string>
            {
                ["CURL_MODE"] = "body",
                ["CURL_BODY"] = """{"text":"x","results":[{"classifier":"rules+llm","intent":"add_task"}]}""",
            });

        Assert.True(result.Rc == 0, $"rc={result.Rc} out={result.Stdout} err={result.Stderr}");
        Assert.Contains("расход a1", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("агент=ask, наш=add_task", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("расхождений с rules+llm: 1 из 1", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Powershell_twin_offers_the_same_commands_and_endpoints()
    {
        string path = TestPaths.Path("tools", "intents.ps1");
        string text = File.ReadAllText(path);

        Assert.Contains("'check'", text, StringComparison.Ordinal);
        Assert.Contains("'compare'", text, StringComparison.Ordinal);
        Assert.Contains("'import'", text, StringComparison.Ordinal);
        Assert.Contains("/api/classifications/", text, StringComparison.Ordinal);
        Assert.Contains("rules+llm", text, StringComparison.Ordinal);
    }

    private static string Row(string id, string text, string intent, double confidence)
        => $$"""{"id":"{{id}}","text":"{{text}}","intent":"{{intent}}","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"note":""}""";

    private sealed record RunResult(int Rc, string Stdout, string Stderr, string CurlCalls);

    private static RunResult RunIntentTool(
        string[] args,
        string[] rows,
        Dictionary<string, string>? env = null)
    {
        string? bash = FindBash()
            ?? throw new InvalidOperationException(
                "bash не найден: поведенческие тесты tools/intents.sh не выполнить (Linux — apt install bash)");

        string script = TestPaths.Path("tools", "intents.sh").Replace('\\', '/');
        string workDir = Directory.CreateTempSubdirectory("kouki-intents-").FullName;
        string stubDir = Directory.CreateTempSubdirectory("kouki-intents-stubs-").FullName;
        string calls = Path.Combine(workDir, "curl-calls.txt");

        File.WriteAllLines(Path.Combine(workDir, "labeled.jsonl"), rows, new UTF8Encoding(false));
        File.WriteAllText(calls, string.Empty);

        // Заглушка curl: пишет свои аргументы в файл и отдаёт заранее заданный ответ.
        string stub = """
            #!/usr/bin/env bash
            printf '%s\n' "$*" >> "$CURL_CALLS"
            if [ "${CURL_MODE:-body}" = "httpcode" ]; then
                printf '%s' "${CURL_CODE:-200}"
            else
                printf '%s' "${CURL_BODY:-{\}}"
            fi
            exit "${CURL_RC:-0}"
            """;
        string stubPath = Path.Combine(stubDir, "curl");
        File.WriteAllText(stubPath, stub.Replace("\r\n", "\n").TrimStart() + "\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var start = new ProcessStartInfo(bash, ["--noprofile", "--norc", script, .. args])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };

        start.Environment["PATH"] = stubDir + (OperatingSystem.IsWindows() ? ";" : ":") + start.Environment["PATH"];
        start.Environment["CURL_CALLS"] = calls;
        start.Environment["HOME"] = workDir;
        foreach ((string key, string value) in env ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
        }

        return new RunResult(process.ExitCode, stdout, stderr,
            File.Exists(calls) ? File.ReadAllText(calls) : string.Empty);
    }

    private static string? FindBash()
    {
        string[] candidates =
        [
            "/usr/bin/bash",
            "/bin/bash",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            "bash",
        ];

        foreach (string candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            foreach (string dir in (path ?? string.Empty).Split(Path.PathSeparator))
            {
                string full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }
}
