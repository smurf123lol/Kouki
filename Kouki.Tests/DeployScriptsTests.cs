using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Kouki.Tests;

/// <summary>
/// Поведенческие проверки смоука и сторожа: скрипты запускаются в настоящем bash
/// с подменёнными <c>curl</c>/<c>systemctl</c>/<c>journalctl</c>. Строковые тесты
/// (<see cref="DeploySmokeAssetsTests"/>) ловят удаление кода, но не логику:
/// «нет токена — это проблема» и «алерт без ok:true не считается доставленным»
/// проходят любую проверку на наличие подстрок.
/// </summary>
public sealed class DeployScriptsTests
{
    [Fact]
    public void Smoke_rejects_url_without_a_value()
    {
        RunResult result = RunSmoke("--url");

        Assert.Equal(2, result.Rc);
        Assert.Contains("требует значения", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_rejects_another_flag_as_a_value()
    {
        RunResult result = RunSmoke("--url", "--json");

        Assert.Equal(2, result.Rc);
        Assert.Contains("требует значения", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_reads_only_by_default_and_never_posts_intake()
    {
        RunResult result = RunSmoke(
            ["--url", "http://127.0.0.1:5300"],
            new Dictionary<string, string> { ["KOUKI_TOKEN"] = "test-token" });

        Assert.DoesNotContain("/api/intake", result.CurlCalls, StringComparison.Ordinal);
        Assert.Contains("/api/health", result.CurlCalls, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_treats_missing_token_as_a_problem()
    {
        RunResult result = RunMonitor(new Dictionary<string, string>
        {
            ["HEALTH_CODE"] = "200",
            ["API_CODE"] = "200",
        });

        Assert.Equal(1, result.Rc);
        Assert.StartsWith("fail:нет токена API", result.State, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_does_not_stamp_when_telegram_says_not_ok()
    {
        RunResult result = RunMonitor(new Dictionary<string, string>
        {
            ["HEALTH_CODE"] = "000",
            ["TG_BODY"] = "{\"ok\":false,\"description\":\"Unauthorized\"}",
            ["TG_RC"] = "0",
            ["KOUKI_ALERT_TOKEN"] = "bot-token",
            ["KOUKI_ALERT_CHAT"] = "42",

            ["KOUKI_TOKEN"] = "api-token",
        });

        Assert.Equal(1, result.Rc);
        Assert.False(result.HasStamp, "отметка last-alert не должна появляться без ok:true");
        Assert.Contains("без ok:true", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_stamps_only_after_confirmed_delivery()
    {
        RunResult result = RunMonitor(new Dictionary<string, string>
        {
            ["HEALTH_CODE"] = "000",
            ["TG_BODY"] = "{\"ok\":true,\"result\":{\"message_id\":1}}",
            ["TG_RC"] = "0",
            ["KOUKI_ALERT_TOKEN"] = "bot-token",
            ["KOUKI_ALERT_CHAT"] = "42",
            ["KOUKI_TOKEN"] = "api-token",
        });

        Assert.Equal(1, result.Rc);
        Assert.True(result.HasStamp, "после ok:true отметка должна появиться");
    }

    [Fact]
    public void Monitor_retries_the_recovery_alert_until_it_is_delivered()
    {
        // Сначала сервер болен и алерт доставлен; потом выздоровел, но Telegram
        // не принял сообщение — маркер восстановления обязан остаться.
        RunResult first = RunMonitor(new Dictionary<string, string>
        {
            ["HEALTH_CODE"] = "000",
            ["TG_BODY"] = "{\"ok\":true}",
            ["TG_RC"] = "0",
            ["KOUKI_ALERT_TOKEN"] = "bot-token",
            ["KOUKI_ALERT_CHAT"] = "42",
            ["KOUKI_TOKEN"] = "api-token",
        });
        Assert.Equal(1, first.Rc);

        RunResult second = RunMonitor(new Dictionary<string, string>
        {
            ["HEALTH_CODE"] = "200",
            ["API_CODE"] = "200",
            ["TG_BODY"] = "{\"ok\":false}",
            ["TG_RC"] = "0",
            ["KOUKI_ALERT_TOKEN"] = "bot-token",
            ["KOUKI_ALERT_CHAT"] = "42",
            ["KOUKI_TOKEN"] = "api-token",
        }, stateDir: first.StateDir);

        Assert.Equal(0, second.Rc);
        Assert.True(File.Exists(Path.Combine(second.StateDir, "recovery-pending")),
            "недоставленное «снова в порядке» должно ждать повтора");

        RunResult third = RunMonitor(new Dictionary<string, string>
        {
            ["HEALTH_CODE"] = "200",
            ["API_CODE"] = "200",
            ["TG_BODY"] = "{\"ok\":true}",
            ["TG_RC"] = "0",
            ["KOUKI_ALERT_TOKEN"] = "bot-token",
            ["KOUKI_ALERT_CHAT"] = "42",
            ["KOUKI_TOKEN"] = "api-token",
        }, stateDir: first.StateDir);

        Assert.Equal(0, third.Rc);
        Assert.False(File.Exists(Path.Combine(third.StateDir, "recovery-pending")),
            "после доставки маркер восстановления снимается");
    }

    private sealed record RunResult(int Rc, string Stdout, string Stderr, string State, bool HasStamp, string CurlCalls, string StateDir);

    private static RunResult RunSmoke(params string[] args)
        => RunSmoke(args, new Dictionary<string, string>());

    private static RunResult RunSmoke(string[] args, Dictionary<string, string> env)
    {
        string script = TestPaths.Path("deploy", "smoke.sh").Replace('\\', '/');
        return RunScript(script, args, env, stateDir: null);
    }

    private static RunResult RunMonitor(Dictionary<string, string> env, string? stateDir = null)
    {
        string script = TestPaths.Path("deploy", "kouki-monitor.sh").Replace('\\', '/');
        string dir = stateDir ?? Directory.CreateTempSubdirectory("kouki-mon-state-").FullName;
        return RunScript(script, ["--state", dir.Replace('\\', '/')], env, dir);
    }

    private static RunResult RunScript(string script, string[] args, Dictionary<string, string> env, string? stateDir)
    {
        string? bash = FindBash();
        if (bash is null)
        {
            throw new InvalidOperationException(
                "bash не найден: поведенческие тесты смоука и сторожа не выполнить " +
                "(Windows — поставьте Git Bash, Linux — apt install bash)");
        }

        string stubDir = Directory.CreateTempSubdirectory("kouki-stubs-").FullName;
        string workDir = Directory.CreateTempSubdirectory("kouki-run-").FullName;
        string tmpDir = Directory.CreateTempSubdirectory("kouki-tmp-").FullName;

        string calls = Path.Combine(workDir, "curl-calls.txt").Replace('\\', '/');
        File.WriteAllText(calls, string.Empty);

        WriteStub(stubDir, "curl", CurlStub);
        WriteStub(stubDir, "systemctl", """
            #!/usr/bin/env bash
            case "$1" in
                is-active|is-enabled) echo "active"; exit 0 ;;
                *) exit 0 ;;
            esac
            """);
        WriteStub(stubDir, "journalctl", """
            #!/usr/bin/env bash
            echo ""
            exit 0
            """);
        WriteStub(stubDir, "mountpoint", """
            #!/usr/bin/env bash
            exit 0
            """);

        // Git Bash понимает только POSIX-пути в PATH, а каталог стенда — windows-путь:
        // конвертацию делает launcher через cygpath (в Linux это no-op).
        string launcher = Path.Combine(workDir, "launcher.sh");
        string launcherText = """
            #!/usr/bin/env bash
            # cygpath есть только в Git Bash; в Linux пути уже POSIX-ные.
            dir="$STUB_DIR"
            script="$KOUKI_SCRIPT"
            if command -v cygpath >/dev/null 2>&1; then
                dir="$(cygpath -u "$dir")"
                script="$(cygpath -u "$script")"
            fi
            export PATH="$dir:$PATH"
            # exec требует +x, а файлы репозитория в git идут 644 — зовём bash явно.
            exec bash "$script" "$@"
            """;
        // Скрипт обязан быть с LF: bash на строке с CR падает, а исходник теста
        // на Windows хранится с CRLF.
        File.WriteAllText(launcher, launcherText.Replace("\r\n", "\n").TrimStart() + "\n",
            new UTF8Encoding(false));

        var start = new ProcessStartInfo(bash, ["--noprofile", "--norc", launcher.Replace('\\', '/'), .. args])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };

        start.Environment["STUB_DIR_WIN"] = stubDir;
        start.Environment["HOME"] = workDir.Replace('\\', '/');
        start.Environment["TMPDIR"] = tmpDir.Replace('\\', '/');
        start.Environment["STUB_DIR"] = stubDir.Replace('\\', '/');
        start.Environment["KOUKI_SCRIPT"] = script;
        start.Environment["CURL_CALLS"] = calls;
        start.Environment["KOUKI_SMOKE_SERVICES"] = "kouki";
        start.Environment["KOUKI_SMOKE_SHARE"] = tmpDir.Replace('\\', '/');
        start.Environment["KOUKI_MONITOR_ENV"] = "/nonexistent/monitor.env";
        start.Environment["KOUKI_SMOKE_DSH"] = "http://127.0.0.1:5300/health";

        foreach ((string key, string value) in env)
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

        int rc = process.ExitCode;
        string statePath = stateDir is null ? string.Empty : Path.Combine(stateDir, "state");
        string state = File.Exists(statePath) ? File.ReadAllText(statePath).Trim() : string.Empty;
        bool hasStamp = stateDir is not null && File.Exists(Path.Combine(stateDir, "last-alert"));

        return new RunResult(rc, stdout, stderr, state, hasStamp,
            File.ReadAllText(calls), stateDir ?? string.Empty);
    }

    private static void WriteStub(string dir, string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content.Replace("\r\n", "\n").TrimStart() + "\n", new UTF8Encoding(false));

        // В Linux заглушку ищут по PATH — без права на исполнение она не найдётся.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private const string CurlStub = """
        #!/usr/bin/env bash
        url=""; out=""; conf=""; fail=0
        while [ $# -gt 0 ]; do
            case "$1" in
                -o) out="$2"; shift 2 ;;
                -w) shift 2 ;;
                -K) conf="$2"; shift 2 ;;
                -d|--data|--data-urlencode|-X|-H|--max-time) shift 2 ;;
                --fail-with-body) fail=1; shift ;;
                -s) shift ;;
                *) url="$1"; shift ;;
            esac
        done
        target="$url"
        if [ -n "$conf" ] && [ -f "$conf" ]; then
            # В конфиге могут быть header и url — берём url, если он там есть.
            configured="$(sed -n 's/^url = "\(.*\)"$/\1/p' "$conf")"
            [ -n "$configured" ] && target="$configured"
        fi
        echo "$target" >>"$CURL_CALLS"
        case "$target" in
            *api.telegram.org*)
                [ -n "$out" ] && printf '%s' "${TG_BODY:-{\"ok\":true}}" >"$out"
                if [ "${TG_RC:-0}" != "0" ] && [ "$fail" = "1" ]; then
                    exit 22
                fi
                exit "${TG_RC:-0}"
                ;;
            */api/health*)
                printf '%s' "${HEALTH_CODE:-200}"
                exit 0
                ;;
            */api/classifiers*)
                printf '%s' "${API_CODE:-200}"
                exit 0
                ;;
            *)
                printf '200'
                exit 0
                ;;
        esac
        """;

    private static string? FindBash()
    {
        // Git Bash — раньше WSL: `C:\Windows\System32\bash.exe` не понимает
        // windows-пути и не умеет cygpath, поэтому стенд под ним молча не работает.
        string[] candidates =
        [
            "/usr/bin/bash",
            "/bin/bash",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
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
            if (path is null)
            {
                continue;
            }

            foreach (string dir in path.Split(Path.PathSeparator))
            {
                string full = Path.Combine(dir, candidate + ".exe");
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }
}
