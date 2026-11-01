using System.Diagnostics;
using System.Text;

namespace Kouki.Tests;

/// <summary>
/// Контракт инструментов деплоя для Linux (T96): публикация без PowerShell и
/// код-деплой без установщика. Shell целиком тестами не покрыть, но свойства,
/// которые ломаются при правках, — можно.
/// </summary>
/// <remarks>
/// Отдельные тесты запускают скрипты в настоящем bash: обработка аргументов
/// происходит до проверки зависимостей (rsync, dotnet), поэтому такие проверки
/// не требуют ни инструментов, ни боевых каталогов.
/// </remarks>
public sealed class DeployToolingAssetsTests
{
    private static string Script(params string[] parts)
        => File.ReadAllText(TestPaths.Path(parts));

    private static string DeployCode() => Script("deploy", "deploy-code.sh");

    private static string PublishLinux() => Script("tools", "publish-linux.sh");

    [Fact]
    public void Publish_builds_all_three_artifacts_and_checks_native_libraries()
    {
        var script = PublishLinux();

        Assert.Contains("Kagedama.api/Kagedama.api.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Kouki.Kagedama.Bot/Kouki.Kagedama.Bot.csproj", script, StringComparison.Ordinal);
        Assert.Contains("Kagedama.mcp/Kagedama.mcp.csproj", script, StringComparison.Ordinal);
        Assert.Contains("KoukiEnableSherpa", script, StringComparison.Ordinal);
        Assert.Contains("libsherpa-onnx-c-api.so", script, StringComparison.Ordinal);
        Assert.Contains("libonnxruntime.so", script, StringComparison.Ordinal);
        Assert.Contains("libe_sqlite3", script, StringComparison.Ordinal);
        Assert.Contains("wwwroot/app.js", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_uses_a_staging_directory_and_keeps_the_previous_artifact()
    {
        var script = PublishLinux();

        // Неудачная сборка не должна уничтожать артефакт, на который можно откатиться:
        // публикация идёт во временный каталог, подмена — только после проверки.
        Assert.Contains("mktemp -d", script, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", script, StringComparison.Ordinal);
        Assert.Contains("$OUTPUT_NORMALIZED.prev", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf \"$OUTPUT\"", script, StringComparison.Ordinal);
        Assert.Contains("--allow-any-output", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_defaults_to_self_contained_like_production()
    {
        var script = PublishLinux();

        Assert.Contains("SELF_CONTAINED=true", script, StringComparison.Ordinal);
        Assert.Contains("--framework-dependent", script, StringComparison.Ordinal);
        Assert.Contains("прод собран self-contained", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_refuses_to_mix_self_contained_framework_dependent_and_speech_shapes()
    {
        var script = DeployCode();

        Assert.Contains("libcoreclr.so", script, StringComparison.Ordinal);
        Assert.Contains("self-contained", script, StringComparison.Ordinal);
        Assert.Contains("--framework-dependent", script, StringComparison.Ordinal);
        Assert.Contains("libsherpa-onnx-c-api.so", script, StringComparison.Ordinal);
        Assert.Contains("--allow-no-speech", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_requires_markers_in_both_targets_and_forbids_nested_paths()
    {
        var script = DeployCode();

        // Заголовок программы — не проверка: каталог MCP обязан иметь свой маркер,
        // иначе --delete доберётся до чужого каталога.
        Assert.Contains("Kagedama.api.dll", script, StringComparison.Ordinal);
        Assert.Contains("Kagedama.mcp.dll", script, StringComparison.Ordinal);
        Assert.Contains("inside()", script, StringComparison.Ordinal);
        Assert.Contains("каталоги app и mcp пересекаются", script, StringComparison.Ordinal);
        Assert.Contains("лежит внутри", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_keeps_bot_and_mcp_out_of_the_api_rsync()
    {
        var script = DeployCode();

        Assert.Contains("--exclude 'bot/'", script, StringComparison.Ordinal);
        Assert.Contains("--exclude 'mcp/'", script, StringComparison.Ordinal);
        Assert.Contains("--exclude 'appsettings.Development.json'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_backs_up_and_rolls_back_on_failure()
    {
        var script = DeployCode();

        Assert.Contains(".prev", script, StringComparison.Ordinal);
        Assert.Contains("rollback()", script, StringComparison.Ordinal);
        Assert.Contains("cp -a \"$APP_DIR_REAL\" \"$PREV_APP\"", script, StringComparison.Ordinal);
        Assert.Contains("Откат к предыдущему артефакту", script, StringComparison.Ordinal);
        // Откат обязан срабатывать и на health, и на смоуке, и говорить правду,
        // когда копии нет (--no-backup): иначе сообщение обещает несуществующий откат.
        Assert.Contains("сервер не поднялся после деплоя", script, StringComparison.Ordinal);
        Assert.Contains("смоук провалился", script, StringComparison.Ordinal);
        Assert.Contains("прод возвращён к предыдущему артефакту", script, StringComparison.Ordinal);
        Assert.Contains("откат отключён (--no-backup): прод остался на новом артефакте", script, StringComparison.Ordinal);
        Assert.Contains("копии нет (--no-backup)", script, StringComparison.Ordinal);
        // Неудавшийся откат нельзя выдавать за успешный.
        Assert.Contains("откат НЕ удался: восстановите вручную", script, StringComparison.Ordinal);
        Assert.Contains("return \"$failed\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_passes_the_api_token_through_the_environment_not_argv()
    {
        var script = DeployCode();

        Assert.Contains("KOUKI_TOKEN=\"$TOKEN\" bash", script, StringComparison.Ordinal);
        Assert.DoesNotContain("smoke.sh\" --token", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_has_a_dry_run_that_changes_nothing()
    {
        var script = DeployCode();

        Assert.Contains("dry-run: ничего не изменено", script, StringComparison.Ordinal);
        // После блока dry-run скрипт обязан выйти, иначе флаг «ничего не менять» врёт.
        Assert.Contains("    exit 0\nfi", script.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Worktree_script_merges_without_push_and_refuses_dirty_trees()
    {
        var script = Script("tools", "worktree.sh");

        Assert.Contains("--no-ff", script, StringComparison.Ordinal);
        Assert.Contains("git -C \"$path\" status --porcelain", script, StringComparison.Ordinal);
        Assert.Contains("дерево задачи грязное", script, StringComparison.Ordinal);
        Assert.Contains("основное дерево грязное", script, StringComparison.Ordinal);
        Assert.DoesNotContain("git push", script, StringComparison.Ordinal);
        Assert.Contains("git worktree remove", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_succeeds_with_a_stub_dotnet_even_in_a_fresh_tree()
    {
        // Скрипт рассчитан на Linux-сервер: в Git Bash на Windows он не проверяется.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-pub-").FullName;
        try
        {
            string stub = Path.Combine(root, "stub");
            Directory.CreateDirectory(stub);
            string stubPath = Path.Combine(stub, "dotnet");
            File.WriteAllText(stubPath, DotnetStub, new UTF8Encoding(false));
            var chmod = RunBash("-c", $"chmod +x '{stubPath}'");
            Assert.Equal(0, chmod.Rc);

            // Родителя ещё нет: именно на этом ломался первый запуск в свежем дереве.
            string output = Path.Combine(root, "fresh", "linux-x64");
            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };

            var first = RunBashWithEnv(TestPaths.Path("tools", "publish-linux.sh"), env, "--output", output);
            Assert.True(first.Rc == 0, $"rc={first.Rc} err={first.Error}");
            Assert.True(File.Exists(Path.Combine(output, "Kagedama.api.dll")), "артефакт не создан");
            Assert.True(Directory.Exists(Path.Combine(output, "bot")), "бот не опубликован");
            Assert.True(Directory.Exists(Path.Combine(output, "mcp")), "MCP не опубликован");

            var second = RunBashWithEnv(TestPaths.Path("tools", "publish-linux.sh"), env, "--output", output);
            Assert.True(second.Rc == 0, $"rc={second.Rc} err={second.Error}");
            Assert.True(
                File.Exists(Path.Combine(output + ".prev", "Kagedama.api.dll")),
                "предыдущий артефакт не сохранён в .prev");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Publish_writes_to_the_default_publish_directory_of_a_fresh_repo()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-fresh-repo-").FullName;
        try
        {
            // Фейковый репозиторий: скрипт считает своё расположение, поэтому копируем
            // его в <repo>/tools и создаём ожидаемые csproj. Каталога publish/ нет —
            // именно этот случай ломался до правки нормализации.
            string tools = Path.Combine(root, "tools");
            Directory.CreateDirectory(tools);
            File.Copy(
                TestPaths.Path("tools", "publish-linux.sh"),
                Path.Combine(tools, "publish-linux.sh"));
            foreach (var project in new[]
                     {
                         "Kagedama.api/Kagedama.api.csproj",
                         "Kouki.Kagedama.Bot/Kouki.Kagedama.Bot.csproj",
                         "Kagedama.mcp/Kagedama.mcp.csproj",
                     })
            {
                string path = Path.Combine(root, project);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "<Project />");
            }

            string stub = Path.Combine(root, "stub");
            Directory.CreateDirectory(stub);
            string stubPath = Path.Combine(stub, "dotnet");
            File.WriteAllText(stubPath, DotnetStub, new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{stubPath}'").Rc);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(Path.Combine(tools, "publish-linux.sh"), env);

            Assert.True(result.Rc == 0, $"rc={result.Rc} err={result.Error}");
            Assert.True(
                File.Exists(Path.Combine(root, "publish", "linux-x64", "Kagedama.api.dll")),
                "дефолтный publish/linux-x64 не создан");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_reports_a_failed_rollback_instead_of_promising_restore()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-rb-fail-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            foreach (var dir in new[]
                     {
                         Path.Combine(artifact, "wwwroot"), Path.Combine(artifact, "bot"),
                         Path.Combine(artifact, "mcp"), Path.Combine(app, "bot"), mcp, stub,
                     })
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(Path.Combine(artifact, "Kagedama.api.dll"), "NEW-API");
            File.WriteAllText(Path.Combine(artifact, "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(artifact, "wwwroot", "app.js"), "x");
            File.WriteAllText(Path.Combine(artifact, "bot", "Kouki.Kagedama.Bot.dll"), "NEW-BOT");
            File.WriteAllText(Path.Combine(artifact, "bot", "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(artifact, "mcp", "Kagedama.mcp.dll"), "NEW-MCP");
            File.WriteAllText(Path.Combine(artifact, "mcp", "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(app, "Kagedama.api.dll"), "OLD-API");
            File.WriteAllText(Path.Combine(app, "bot", "Kouki.Kagedama.Bot.dll"), "OLD-BOT");
            File.WriteAllText(Path.Combine(mcp, "Kagedama.mcp.dll"), "OLD-MCP");

            // Подставной sudo: валит restart и одновременно лишает копию прав,
            // чтобы rsync отката не смог её вернуть.
            string sudoPath = Path.Combine(stub, "sudo");
            File.WriteAllText(sudoPath, """
                #!/usr/bin/env bash
                case "$*" in
                  *restart-kouki*)
                    if [ -n "${ROLLBACK_PREV:-}" ] && [ -d "$ROLLBACK_PREV" ]; then
                      chmod -R 000 "$ROLLBACK_PREV" 2>/dev/null || true
                    fi
                    exit 1 ;;
                esac
                exit 0
                """, new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{sudoPath}'").Rc);

            string deploy = TestPaths.Path("deploy", "deploy-code.sh");
            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
                ["ROLLBACK_PREV"] = Path.Combine(app + ".prev"),
            };
            var result = RunBashWithEnv(
                deploy, env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("откат НЕ удался", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("прод возвращён к предыдущему артефакту", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            // Снимаем 000, иначе временный каталог не удалить.
            RunBash("-c", $"chmod -R u+rwX '{root}' 2>/dev/null || true");
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_rejects_unknown_arguments_and_flag_like_values()
    {
        var (rc, _, err) = RunBashWithEnv(TestPaths.Path("deploy", "deploy-code.sh"), new Dictionary<string, string>(), "--bogus");
        Assert.Equal(2, rc);
        Assert.Contains("неизвестный аргумент", err, StringComparison.Ordinal);

        // `--from --dry-run` не должно принимать флаг за путь.
        var (rc2, _, err2) = RunBashWithEnv(TestPaths.Path("deploy", "deploy-code.sh"), new Dictionary<string, string>(), "--from", "--dry-run");
        Assert.Equal(2, rc2);
        Assert.Contains("похоже на флаг", err2, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_rejects_unknown_arguments_and_flag_like_values()
    {
        var (rc, _, err) = RunBashWithEnv(TestPaths.Path("tools", "publish-linux.sh"), new Dictionary<string, string>(), "--runtime", "--output");
        Assert.Equal(2, rc);
        Assert.Contains("похоже на флаг", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_tooling_scripts_answer_help()
    {
        foreach (var path in new[] { new[] { "tools", "publish-linux.sh" }, new[] { "deploy", "deploy-code.sh" } })
        {
            var (rc, output, _) = RunBashWithEnv(TestPaths.Path(path), new Dictionary<string, string>(), "--help");

            Assert.Equal(0, rc);
            Assert.Contains("Использование:", output, StringComparison.Ordinal);

            var head = File.ReadAllBytes(TestPaths.Path(path)).Take(2).ToArray();
            Assert.Equal((byte)'#', head[0]);
            Assert.Equal((byte)'!', head[1]);
        }
    }

    /// <summary>Подставной dotnet: создаёт обязательные файлы артефакта и выходит с нулём.</summary>
    private const string DotnetStub = """
        #!/usr/bin/env bash
        out=""
        prev=""
        for a in "$@"; do
          if [ "$prev" = "-o" ]; then out="$a"; fi
          prev="$a"
        done
        [ -n "$out" ] || exit 3
        mkdir -p "$out/wwwroot" "$out/bot" "$out/mcp"
        printf x > "$out/Kagedama.api.dll"
        printf '{}' > "$out/appsettings.json"
        printf x > "$out/wwwroot/app.js"
        printf x > "$out/bot/Kouki.Kagedama.Bot.dll"
        printf '{}' > "$out/bot/appsettings.json"
        printf x > "$out/mcp/Kagedama.mcp.dll"
        printf '{}' > "$out/mcp/appsettings.json"
        printf x > "$out/libonnxruntime.so"
        printf x > "$out/libsherpa-onnx-c-api.so"
        printf x > "$out/libe_sqlite3.so"
        exit 0
        """;

    private static (int Rc, string Output, string Error) RunBash(params string[] args)
        => RunBashWithEnv(null, new Dictionary<string, string>(), args);

    private static (int Rc, string Output, string Error) RunBashWithEnv(
        string? scriptPath,
        Dictionary<string, string> env,
        params string[] args)
    {
        string? bash = FindBash();
        if (bash is null)
        {
            throw new InvalidOperationException(
                "bash не найден: поведенческие тесты инструментов деплоя не выполнить " +
                "(Windows — поставьте Git Bash, Linux — apt install bash)");
        }

        var start = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--noprofile");
        start.ArgumentList.Add("--norc");
        if (scriptPath is not null)
        {
            start.ArgumentList.Add(scriptPath.Replace('\\', '/'));
        }
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach ((string key, string value) in env)
        {
            start.Environment[key] = value;
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            throw new TimeoutException($"{scriptPath} не завершился за 30 с");
        }

        return (process.ExitCode, output, error);
    }

    private static string? FindBash()
    {
        foreach (var candidate in new[]
                 {
                     "/usr/bin/bash", "/bin/bash",
                     @"C:\Program Files\Git\bin\bash.exe",
                     @"C:\Program Files\Git\usr\bin\bash.exe",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
