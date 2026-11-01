using System.Diagnostics;

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
        // Откат обязан срабатывать и на health, и на смоуке.
        Assert.Contains("сервер не поднялся после деплоя", script, StringComparison.Ordinal);
        Assert.Contains("смоук провалился — прод возвращён", script, StringComparison.Ordinal);
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
    public void Deploy_rejects_unknown_arguments_and_flag_like_values()
    {
        var (rc, _, err) = RunBash(TestPaths.Path("deploy", "deploy-code.sh"), "--bogus");
        Assert.Equal(2, rc);
        Assert.Contains("неизвестный аргумент", err, StringComparison.Ordinal);

        // `--from --dry-run` не должно принимать флаг за путь.
        var (rc2, _, err2) = RunBash(TestPaths.Path("deploy", "deploy-code.sh"), "--from", "--dry-run");
        Assert.Equal(2, rc2);
        Assert.Contains("похоже на флаг", err2, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_rejects_unknown_arguments_and_flag_like_values()
    {
        var (rc, _, err) = RunBash(TestPaths.Path("tools", "publish-linux.sh"), "--runtime", "--output");
        Assert.Equal(2, rc);
        Assert.Contains("похоже на флаг", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_tooling_scripts_answer_help()
    {
        foreach (var path in new[] { new[] { "tools", "publish-linux.sh" }, new[] { "deploy", "deploy-code.sh" } })
        {
            var (rc, output, _) = RunBash(TestPaths.Path(path), "--help");

            Assert.Equal(0, rc);
            Assert.Contains("Использование:", output, StringComparison.Ordinal);

            var head = File.ReadAllBytes(TestPaths.Path(path)).Take(2).ToArray();
            Assert.Equal((byte)'#', head[0]);
            Assert.Equal((byte)'!', head[1]);
        }
    }

    private static (int Rc, string Output, string Error) RunBash(string scriptPath, params string[] args)
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
        start.ArgumentList.Add(scriptPath.Replace('\\', '/'));
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
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
