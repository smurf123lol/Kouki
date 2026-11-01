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
        // Прерывание в окне подмены не должно оставлять OUTPUT отсутствующим.
        Assert.Contains("trap interrupted INT TERM HUP", script, StringComparison.Ordinal);
        Assert.Contains("mv \"$PREV_OUTPUT\" \"$OUTPUT_NORMALIZED\"", script, StringComparison.Ordinal);
        Assert.Contains("$OUTPUT_NORMALIZED.prev", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf \"$OUTPUT\"", script, StringComparison.Ordinal);
        Assert.Contains("--allow-any-output", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_passes_distinct_output_directories_to_each_publish()
    {
        // DotnetStub создаёт все маркеры при любом -o, поэтому перепутанные каталоги
        // поведенческим тестом не ловятся — их фиксирует статическая проверка.
        var script = PublishLinux();

        Assert.Contains("-o \"$STAGE\" || die \"dotnet publish API", script, StringComparison.Ordinal);
        Assert.Contains("-o \"$STAGE/bot\" || die", script, StringComparison.Ordinal);
        Assert.Contains("-o \"$STAGE/mcp\" || die", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Worktree_finds_a_tree_by_an_underscored_name()
    {
        // Major 3 раунда 7: new заменяет `_` на `-`, а remove/merge искали по сырому
        // имени — `remove --name kouki_t1_demo` не находил дерево `kouki-t1-demo`.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-wt-underscore-").FullName;
        try
        {
            string repo = Path.Combine(root, "Kouki");
            Directory.CreateDirectory(Path.Combine(repo, "tools"));
            File.Copy(TestPaths.Path("tools", "worktree.sh"), Path.Combine(repo, "tools", "worktree.sh"));
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "test@example.com",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "test@example.com",
            };
            Assert.Equal(0, RunBashWithEnv(null, env, "-c", $"cd '{repo}' && git init -q -b master && git add -A && git commit -qm init").Rc);

            string script = Path.Combine(repo, "tools", "worktree.sh");
            var created = RunBashWithEnv(script, env, "new", "--task", "T1", "--slug", "demo");
            Assert.True(created.Rc == 0, $"rc={created.Rc} err={created.Error}");
            string tree = Path.Combine(root, "_scratch", "wt", "kouki-t1-demo");
            Assert.True(Directory.Exists(tree), "дерево не создано");

            // merge по имени с подчёркиванием должен найти ветку и влить её в master.
            File.AppendAllText(Path.Combine(tree, "note.txt"), "from task\n");
            Assert.Equal(0, RunBashWithEnv(null, env, "-c", $"cd '{tree}' && git add -A && git commit -qm task").Rc);
            var merged = RunBashWithEnv(script, env, "merge", "--name", "kouki_t1_demo");
            Assert.True(merged.Rc == 0, $"merge rc={merged.Rc} out={merged.Output} err={merged.Error}");
            Assert.Contains("влита в master", merged.Output, StringComparison.Ordinal);

            var removed = RunBashWithEnv(script, env, "remove", "--name", "kouki_t1_demo");
            Assert.True(removed.Rc == 0, $"remove rc={removed.Rc} out={removed.Output} err={removed.Error}");
            Assert.Contains("Дерево удалено", removed.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(tree), "дерево не удалено");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Worktree_resolves_names_case_insensitively_like_new()
    {
        var script = Script("tools", "worktree.sh");

        // new создаёт имя в нижнем регистре и меняет `_` на `-`, значит merge/remove
        // обязаны искать по тому же правилу, иначе дерево «не находится».
        Assert.Equal(
            2,
            script.Split(new[] { "name_lower=\"${name_lower//_/-}\"" }, StringSplitOptions.None).Length - 1);
        Assert.Contains("name=\"${name//_/-}\"", script, StringComparison.Ordinal);
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

        Assert.Contains("KOUKI_TOKEN=\"$TOKEN\" KOUKI_SMOKE_SINCE=\"$SMOKE_SINCE\" bash", script, StringComparison.Ordinal);
        // Токен не должен попадать в argv ни одного дочернего процесса: ни в смоук,
        // ни в kouki-admin. Проверяем не конкретную строку, а отсутствие флага.
        Assert.DoesNotContain("--token", script, StringComparison.Ordinal);
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
            CreateDeployShape(artifact, app, mcp, stub);

            // rsync отката (источник — .prev) падает, restart тоже: смоук не должен
            // обещать «прод возвращён». Не chmod 000 — под root он не сработал бы.
            string rsyncPath = Path.Combine(stub, "rsync");
            File.WriteAllText(rsyncPath, "#!/usr/bin/env bash\ncase \"$*\" in\n  *.prev*) exit 23 ;;\nesac\nexec /usr/bin/rsync \"$@\"\n", new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{rsyncPath}'").Rc);

            string sudoPath = Path.Combine(stub, "sudo");
            File.WriteAllText(
                sudoPath,
                "#!/usr/bin/env bash\ncase \"$*\" in\n  *restart*) exit 1 ;;\nesac\nexit 0\n",
                new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{sudoPath}'").Rc);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("откат НЕ удался", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("прод возвращён к предыдущему артефакту", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_rolls_back_when_a_target_rsync_fails()
    {
        // Регрессия M1: `rollback` последней командой `||`-группы под set -e гасил `die`,
        // и скрипт выходил с кодом отката, не сказав, что именно сломалось.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-rsync-fail-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);

            // rsync валит только раскладку артефакта в app: откат идёт из app.prev и проходит.
            string rsyncPath = Path.Combine(stub, "rsync");
            File.WriteAllText(rsyncPath, "#!/usr/bin/env bash\ncase \"$*\" in\n  *artifact*app*) exit 23 ;;\nesac\nexec /usr/bin/rsync \"$@\"\n", new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{rsyncPath}'").Rc);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("rsync API завершился с ошибкой", result.Error, StringComparison.Ordinal);
            Assert.Contains("прод возвращён к предыдущему артефакту", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_an_mcp_target_without_marker_before_writing()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-mcp-marker-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            // Цель MCP без маркера: --delete в ней снёс бы чужой каталог.
            File.Delete(Path.Combine(mcp, "Kagedama.mcp.dll"));
            File.WriteAllText(Path.Combine(mcp, "stranger.dll"), "NOT-MCP");

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("не похож на MCP-сервер (нет Kagedama.mcp.dll)", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
            Assert.False(Directory.Exists(app + ".prev"), "до проверки маркеров копия создаваться не должна");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_an_app_target_without_marker_before_writing()
    {
        // Major 2 раунда 7: проверка app-маркера держалась только на статике —
        // без неё --delete мог снести чужой каталог.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-app-marker-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            // Цель без маркера, но с рантаймом — на первый взгляд «сервер».
            File.Delete(Path.Combine(app, "Kagedama.api.dll"));
            File.WriteAllText(Path.Combine(app, "stranger.dll"), "NOT-OURS");

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("не похож на установленный сервер (нет Kagedama.api.dll)", result.Error, StringComparison.Ordinal);
            Assert.Equal("NOT-OURS", File.ReadAllText(Path.Combine(app, "stranger.dll")));
            Assert.False(Directory.Exists(app + ".prev"), "до проверки маркеров копия создаваться не должна");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Kagedama.api.dll")]
    [InlineData("appsettings.json")]
    [InlineData("wwwroot/app.js")]
    [InlineData("bot/Kouki.Kagedama.Bot.dll")]
    [InlineData("bot/appsettings.json")]
    [InlineData("mcp/Kagedama.mcp.dll")]
    [InlineData("mcp/appsettings.json")]
    public void Deploy_refuses_an_artifact_missing_any_required_file(string missing)
    {
        // Major 2 раунда 7: цикл маркеров артефакта не исполнялся ни одним тестом —
        // чужой self-contained каталог принимался и затирал прод через --delete.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-artifact-marker-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            File.Delete(Path.Combine(artifact, missing.Replace('/', Path.DirectorySeparatorChar)));

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains($"в артефакте нет {missing}", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
            Assert.Equal("OLD-MCP", File.ReadAllText(Path.Combine(mcp, "Kagedama.mcp.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_to_copy_a_self_contained_artifact_over_a_framework_dependent_target()
    {
        // Major 2 раунда 7: строка :142 проверялась только по тексту.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-form-fd-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            // Прод framework-dependent: рантайм системный, libcoreclr.so рядом нет.
            File.Delete(Path.Combine(app, "libcoreclr.so"));

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("framework-dependent сборка, а в артефакте — self-contained", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(app, "libcoreclr.so")), "рантайм не должен появиться в framework-dependent проде");
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_to_copy_a_framework_dependent_artifact_over_a_self_contained_target()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-form-sc-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            File.Delete(Path.Combine(artifact, "libcoreclr.so"));

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("self-contained сборка, а в артефакте — framework-dependent", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-RUNTIME", File.ReadAllText(Path.Combine(app, "libcoreclr.so")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_to_mix_the_mcp_form()
    {
        // Минор раунда 7: форма проверялась только для app, а mcp тоже rsync --delete.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-mcp-form-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            File.WriteAllText(Path.Combine(mcp, "libcoreclr.so"), "MCP-RUNTIME");

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("форма MCP не совпадает", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-MCP", File.ReadAllText(Path.Combine(mcp, "Kagedama.mcp.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_when_mcp_dir_is_inside_the_app_dir()
    {
        // Major 2 раунда 7: inside() проверял только статический текст.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-nested-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            string nested = Path.Combine(app, "mcp");
            Directory.CreateDirectory(nested);
            File.Move(Path.Combine(mcp, "Kagedama.mcp.dll"), Path.Combine(nested, "Kagedama.mcp.dll"));

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", nested);

            Assert.Equal(1, result.Rc);
            Assert.Contains("каталоги app и mcp пересекаются", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_an_artifact_that_lives_inside_the_app_dir()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-artifact-inside-").FullName;
        string app = Path.Combine(root, "app");
        string artifact = Path.Combine(app, "artifact");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("лежит внутри цели деплоя", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_does_not_restart_services_on_rollback_with_no_restart()
    {
        // Major 1 раунда 7: откат при --no-restart перезапускал сервисы и мог
        // объявить «откат НЕ удался» при честно восстановленных файлах.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-no-restart-rb-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        string sudoLog = Path.Combine(root, "sudo.log");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);

            string rsyncPath = Path.Combine(stub, "rsync");
            File.WriteAllText(rsyncPath, "#!/usr/bin/env bash\ncase \"$*\" in\n  *artifact*app*) exit 23 ;;\nesac\nexec /usr/bin/rsync \"$@\"\n", new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{rsyncPath}'").Rc);

            string sudoPath = Path.Combine(stub, "sudo");
            File.WriteAllText(sudoPath, "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >> \"$SUDO_LOG\"\nexit 0\n", new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{sudoPath}'").Rc);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
                ["SUDO_LOG"] = sudoLog,
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp, "--no-restart");

            Assert.Equal(1, result.Rc);
            Assert.Contains("прод возвращён к предыдущему артефакту", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
            string log = File.Exists(sudoLog) ? File.ReadAllText(sudoLog) : "";
            Assert.DoesNotContain("restart", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_reports_disabled_rollback_when_restart_fails_without_backup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-no-backup-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);

            string sudoPath = Path.Combine(stub, "sudo");
            File.WriteAllText(sudoPath, "#!/usr/bin/env bash\ncase \"$*\" in\n  *restart-kouki*) exit 1 ;;\nesac\nexit 0\n", new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{sudoPath}'").Rc);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp, "--no-backup");

            Assert.Equal(1, result.Rc);
            Assert.Contains("restart-kouki завершился с ошибкой", result.Error, StringComparison.Ordinal);
            Assert.Contains("откат отключён (--no-backup): прод остался на новом артефакте", result.Error, StringComparison.Ordinal);
            Assert.Equal("NEW-API-CONTENT", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Publish_keeps_the_previous_artifact_when_dotnet_fails()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-pub-fail-").FullName;
        try
        {
            string stub = Path.Combine(root, "stub");
            Directory.CreateDirectory(stub);
            string stubPath = Path.Combine(stub, "dotnet");
            File.WriteAllText(stubPath, "#!/usr/bin/env bash\necho 'dotnet publish failed' >&2\nexit 1\n", new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{stubPath}'").Rc);

            string output = Path.Combine(root, "out", "linux-x64");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "Kagedama.api.dll"), "OLD-API");

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(TestPaths.Path("tools", "publish-linux.sh"), env, "--output", output);

            Assert.Equal(1, result.Rc);
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(output, "Kagedama.api.dll")));
            Assert.False(Directory.Exists(output + ".prev"), "неудачная сборка не должна трогать .prev");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Worktree_merge_refuses_a_dirty_task_tree()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-wt-dirty-").FullName;
        try
        {
            string repo = Path.Combine(root, "Kouki");
            Directory.CreateDirectory(Path.Combine(repo, "tools"));
            File.Copy(TestPaths.Path("tools", "worktree.sh"), Path.Combine(repo, "tools", "worktree.sh"));
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "test@example.com",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "test@example.com",
            };

            Assert.Equal(0, RunBashWithEnv(null, env, "-c", $"cd '{repo}' && git init -q -b master && git add -A && git commit -qm init").Rc);

            string script = Path.Combine(repo, "tools", "worktree.sh");
            var created = RunBashWithEnv(script, env, "new", "--task", "T1", "--slug", "demo");
            Assert.True(created.Rc == 0, $"rc={created.Rc} err={created.Error}");

            string tree = Path.Combine(root, "_scratch", "wt", "kouki-t1-demo");
            File.AppendAllText(Path.Combine(tree, "tools", "worktree.sh"), "\n# dirty\n");
            string masterBefore = RunBashWithEnv(null, env, "-c", $"cd '{repo}' && git rev-parse master").Output.Trim();

            var merged = RunBashWithEnv(script, env, "merge", "--name", "kouki-t1-demo");
            Assert.Equal(1, merged.Rc);
            Assert.Contains("дерево задачи грязное", merged.Error, StringComparison.Ordinal);
            Assert.Equal(masterBefore, RunBashWithEnv(null, env, "-c", $"cd '{repo}' && git rev-parse master").Output.Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Smoke_scopes_the_bot_journal_to_the_deploy_start()
    {
        // Блокер раунда 5: смоук смотрел журнал бота за 2 часа и валил деплой из-за
        // ошибок, случившихся до выката (сразу после перезагрузки).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-smoke-since-").FullName;
        string stub = Path.Combine(root, "stub");
        string argsFile = Path.Combine(root, "journal-args.txt");
        string journalFile = Path.Combine(root, "journal.txt");
        try
        {
            Directory.CreateDirectory(stub);
            WriteStub(stub, "journalctl", "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >> \"$ARGS_FILE\"\ncat \"$JOURNAL_FILE\" 2>/dev/null\nexit 0\n");
            WriteStub(stub, "systemctl", "#!/usr/bin/env bash\ncase \"$*\" in\n  *is-enabled*) printf 'enabled\\n' ;;\n  *) printf 'active\\n' ;;\nesac\nexit 0\n");
            WriteStub(stub, "curl", "#!/usr/bin/env bash\nexit 0\n");
            Directory.CreateDirectory(Path.Combine(root, "share"));
            // Штатная строка рестарта бота: старый PID пишет её на SIGTERM.
            File.WriteAllText(journalFile, "getUpdates: обрыв связи (TaskCanceledException), повтор 1/3\n");

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
                ["KOUKI_TOKEN"] = "test-token",
                ["KOUKI_SMOKE_SINCE"] = "1700000000",
                ["KOUKI_SMOKE_SHARE"] = Path.Combine(root, "share"),
                ["ARGS_FILE"] = argsFile,
                ["JOURNAL_FILE"] = journalFile,
            };

            var withSince = RunBashWithEnv(TestPaths.Path("deploy", "smoke.sh"), env);
            string first = File.ReadAllText(argsFile);
            Assert.Contains("--since @1700000000", first, StringComparison.Ordinal);
            Assert.DoesNotContain("-2 hours", first, StringComparison.Ordinal);
            // Штатный ретрай бота — не провал деплоя.
            Assert.Contains("бот без ошибок с начала деплоя", withSince.Output, StringComparison.Ordinal);

            File.WriteAllText(journalFile,
                "getUpdates: обрыв связи (TaskCanceledException), повтор 1/3\n" +
                "Ошибка синхронизации задач\n");
            var real = RunBashWithEnv(TestPaths.Path("deploy", "smoke.sh"), env);
            Assert.Contains("ошибок в журнале с начала деплоя: 1", real.Output, StringComparison.Ordinal);

            // Пустой журнал — не «без ошибок»: проверка ничего не увидела.
            File.WriteAllText(journalFile, string.Empty);
            var empty = RunBashWithEnv(TestPaths.Path("deploy", "smoke.sh"), env);
            Assert.Contains("журнал пуст с начала деплоя", empty.Output, StringComparison.Ordinal);

            File.Delete(argsFile);
            var without = new Dictionary<string, string>(env);
            without.Remove("KOUKI_SMOKE_SINCE");
            RunBashWithEnv(TestPaths.Path("deploy", "smoke.sh"), without);
            Assert.Contains("--since -2 hours", File.ReadAllText(argsFile), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Smoke_rejects_a_non_numeric_smoke_window()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var env = new Dictionary<string, string> { ["KOUKI_SMOKE_SINCE"] = "abracadabra" };
        var result = RunBashWithEnv(TestPaths.Path("deploy", "smoke.sh"), env, "--quiet");

        Assert.Equal(2, result.Rc);
        Assert.Contains("KOUKI_SMOKE_SINCE должен быть epoch-секундами", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_updates_all_targets_and_keeps_mcp_out_of_the_app_directory()
    {
        // M1 раунда 6: статическая проверка исключений проходила, даже если убрать
        // --exclude из настоящего rsync. Здесь rsync настоящий.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-happy-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            // Артефакт содержит подкаталог mcp/ (как настоящий publish); он не должен
            // оказаться внутри /opt/kouki/app.
            File.WriteAllText(Path.Combine(artifact, "mcp", "marker"), "x");
            // Минор раунда 7: --exclude 'appsettings.Development.json' не проверялся
            // поведением — dev-настройки не должны попадать в прод.
            File.WriteAllText(Path.Combine(artifact, "appsettings.Development.json"), "{}");

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp, "--no-restart");

            Assert.True(result.Rc == 0, $"rc={result.Rc} err={result.Error}");
            Assert.Equal("NEW-API-CONTENT", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
            Assert.Equal("NEW-BOT-CONTENT", File.ReadAllText(Path.Combine(app, "bot", "Kouki.Kagedama.Bot.dll")));
            Assert.Equal("NEW-MCP-CONTENT", File.ReadAllText(Path.Combine(mcp, "Kagedama.mcp.dll")));
            Assert.False(Directory.Exists(Path.Combine(app, "mcp")), "mcp/ попал в API-каталог");
            Assert.False(File.Exists(Path.Combine(app, "appsettings.Development.json")), "dev-настройки скопированы в прод");
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app + ".prev", "Kagedama.api.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_rolls_back_when_interrupted_mid_rsync()
    {
        // M2 раунда 6: trap INT/TERM не исполнялся ни одним тестом.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-int-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);

            // Подставной rsync посылает SIGINT родителю во время раскладки API:
            // trap должен откатить уже скопированные файлы.
            string rsyncPath = Path.Combine(stub, "rsync");
            File.WriteAllText(
                rsyncPath,
                "#!/usr/bin/env bash\ncase \"$*\" in\n  *--dry-run*) exec /usr/bin/rsync \"$@\" ;;\n  *artifact*app*) kill -INT \"$PPID\" 2>/dev/null; sleep 0.2; exit 0 ;;\nesac\nexec /usr/bin/rsync \"$@\"\n",
                new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{rsyncPath}'").Rc);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("деплой прерван (INT/TERM/HUP)", result.Error, StringComparison.Ordinal);
            Assert.Contains("прод возвращён к предыдущему артефакту", result.Error, StringComparison.Ordinal);
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_refuses_to_start_without_kouki_admin_sudo()
    {
        // Minor раунда 6: без sudoers скрипт копировал 300+ МБ и падал на рестарте.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("kouki-sudo-").FullName;
        string artifact = Path.Combine(root, "artifact");
        string app = Path.Combine(root, "app");
        string mcp = Path.Combine(root, "mcp");
        string stub = Path.Combine(root, "stub");
        try
        {
            CreateDeployShape(artifact, app, mcp, stub);
            string sudoPath = Path.Combine(stub, "sudo");
            File.WriteAllText(sudoPath, "#!/usr/bin/env bash\nexit 1\n", new UTF8Encoding(false));
            Assert.Equal(0, RunBash("-c", $"chmod +x '{sudoPath}'").Rc);

            var env = new Dictionary<string, string>
            {
                ["PATH"] = stub + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            };
            var result = RunBashWithEnv(
                TestPaths.Path("deploy", "deploy-code.sh"), env,
                "--from", artifact, "--app-dir", app, "--mcp-dir", mcp);

            Assert.Equal(1, result.Rc);
            Assert.Contains("нет sudo -n kouki-admin status", result.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(app + ".prev"), "проверка sudo должна идти до копии");
            Assert.Equal("OLD-API", File.ReadAllText(Path.Combine(app, "Kagedama.api.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_passes_the_smoke_window_from_the_start_of_the_rollout()
    {
        var deploy = DeployCode();
        var smoke = Script("deploy", "smoke.sh");

        Assert.Contains("SMOKE_SINCE=$(date +%s)", deploy, StringComparison.Ordinal);
        Assert.Contains("KOUKI_SMOKE_SINCE=\"$SMOKE_SINCE\"", deploy, StringComparison.Ordinal);
        Assert.Contains("KOUKI_SMOKE_SINCE", smoke, StringComparison.Ordinal);
        Assert.Contains("BOT_JOURNAL_ARGS=(--since \"@$KOUKI_SMOKE_SINCE\")", smoke, StringComparison.Ordinal);
        Assert.Contains("BOT_IGNORE_RE=", smoke, StringComparison.Ordinal);
        Assert.Contains("trap interrupted INT TERM HUP", deploy, StringComparison.Ordinal);
    }

    private static void WriteStub(string dir, string name, string body)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, body, new UTF8Encoding(false));
        Assert.Equal(0, RunBash("-c", $"chmod +x '{path}'").Rc);
    }

    /// <summary>Артефакт, цели деплоя со старыми файлами и каталог для подставных команд.</summary>
    private static void CreateDeployShape(string artifact, string app, string mcp, string stub)
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(artifact, "wwwroot"), Path.Combine(artifact, "bot"),
                     Path.Combine(artifact, "mcp"), Path.Combine(app, "bot"), mcp, stub,
                 })
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(Path.Combine(artifact, "Kagedama.api.dll"), "NEW-API-CONTENT");
        File.WriteAllText(Path.Combine(artifact, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(artifact, "wwwroot", "app.js"), "x");
        File.WriteAllText(Path.Combine(artifact, "bot", "Kouki.Kagedama.Bot.dll"), "NEW-BOT-CONTENT");
        File.WriteAllText(Path.Combine(artifact, "bot", "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(artifact, "mcp", "Kagedama.mcp.dll"), "NEW-MCP-CONTENT");
        File.WriteAllText(Path.Combine(artifact, "mcp", "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(artifact, "libcoreclr.so"), "x");
        File.WriteAllText(Path.Combine(artifact, "libsherpa-onnx-c-api.so"), "x");
        File.WriteAllText(Path.Combine(artifact, "libonnxruntime.so"), "x");
        File.WriteAllText(Path.Combine(app, "Kagedama.api.dll"), "OLD-API");
        File.WriteAllText(Path.Combine(app, "libcoreclr.so"), "OLD-RUNTIME");
        File.WriteAllText(Path.Combine(app, "bot", "Kouki.Kagedama.Bot.dll"), "OLD-BOT");
        File.WriteAllText(Path.Combine(mcp, "Kagedama.mcp.dll"), "OLD-MCP");

        string sudoPath = Path.Combine(stub, "sudo");
        File.WriteAllText(sudoPath, "#!/usr/bin/env bash\nexit 0\n", new UTF8Encoding(false));
        Assert.Equal(0, RunBash("-c", $"chmod +x '{sudoPath}'").Rc);
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

        // Наследованное окружение разработчика не должно влиять на поведение скриптов:
        // иначе KOUKI_SMOKE_SINCE=… в оболочке ломает тесты смоука.
        foreach (string key in start.Environment.Keys.ToList())
        {
            if (key.StartsWith("KOUKI_", StringComparison.Ordinal))
            {
                start.Environment.Remove(key);
            }
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
