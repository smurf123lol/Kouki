using System.Diagnostics;
using System.Text;

namespace Kouki.Tests;

/// <summary>
/// Контракт `tools/review.sh` (T109): пакет для ревью собирается из правильного
/// репозитория, а пустой дифф не выдаётся за нормальный результат.
/// </summary>
/// <remarks>
/// Тесты запускают настоящий bash в одноразовом git-репозитории: проверяется
/// поведение сценария (коды выхода, содержимое пакета), а не текст скрипта.
/// </remarks>
public sealed class ReviewToolingTests
{
    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "kouki-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "notes-updated"));
            File.WriteAllText(
                Path.Combine(Root, "notes-updated", "Задачи Kouki.md"),
                "| T1 | тестовая задача | открыта |\n",
                new UTF8Encoding(false));
            Repo = Path.Combine(Root, "Kouki");
            Directory.CreateDirectory(Repo);
        }

        public string Root { get; }

        public string Repo { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Временный каталог: не мешает результату теста.
            }
        }
    }

    [Fact]
    public void Review_resolves_repo_relative_to_workspace_from_inside_repo()
    {
        string bash = FindBash();
        using var workspace = new TempWorkspace();
        Git(workspace.Repo, "init", "-q", "-b", "master");
        File.WriteAllText(Path.Combine(workspace.Repo, "a.txt"), "a\n");
        Git(workspace.Repo, "add", "a.txt");
        Git(workspace.Repo, "commit", "-q", "-m", "base");
        Git(workspace.Repo, "checkout", "-q", "-b", "task/t1");
        File.WriteAllText(Path.Combine(workspace.Repo, "b.txt"), "b\n");
        Git(workspace.Repo, "add", "b.txt");
        Git(workspace.Repo, "commit", "-q", "-m", "change");

        string output = Path.Combine(workspace.Root, "_scratch", "review", "packet.md");

        // Каталог репозитория передаётся именем («Kouki»), а cwd — внутри этого же
        // репозитория: относительный путь от cwd не разрешается, а от корня воркспейса — да.
        var result = RunScript(
            bash,
            TestPaths.Path("tools", "review.sh"),
            workspace.Repo,
            "-t", "T1", "-b", "master", "-H", "HEAD", "-r", "Kouki", "-o", output);

        Assert.True(result.Rc == 0, $"rc={result.Rc}, stderr={result.Error}");
        string packet = File.ReadAllText(output);
        Assert.Contains("b.txt", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("Дифф пуст", packet, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_fails_loudly_when_diff_is_empty()
    {
        string bash = FindBash();
        using var workspace = new TempWorkspace();
        Git(workspace.Repo, "init", "-q", "-b", "master");
        File.WriteAllText(Path.Combine(workspace.Repo, "a.txt"), "a\n");
        Git(workspace.Repo, "add", "a.txt");
        Git(workspace.Repo, "commit", "-q", "-m", "base");

        string output = Path.Combine(workspace.Root, "_scratch", "review", "empty.md");

        // База и голова совпадают: изменений нет. Молчаливый «пакет» здесь опасен —
        // его легко принять за ревью, поэтому скрипт обязан предупредить и вернуть 3.
        var result = RunScript(
            bash,
            TestPaths.Path("tools", "review.sh"),
            workspace.Repo,
            "-t", "T1", "-b", "master", "-H", "master", "-r", ".", "-o", output);

        Assert.Equal(3, result.Rc);
        Assert.Contains("Дифф пуст", result.Error, StringComparison.Ordinal);
        string packet = File.ReadAllText(output);
        Assert.Contains("Дифф пуст", packet, StringComparison.Ordinal);
    }

    private static (int Rc, string Out, string Error) RunScript(
        string bash, string scriptPath, string cwd, params string[] args)
    {
        Assert.True(File.Exists(scriptPath), $"нет скрипта: {scriptPath}");

        var start = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd,
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
        if (!process.WaitForExit(60_000))
        {
            throw new TimeoutException($"review.sh не завершился за 60 с: {error}");
        }

        return (process.ExitCode, output, error);
    }

    private static void Git(string repo, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repo,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("user.name=Kouki Agent");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("user.email=agent@kouki.local");
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
    }

    private static string FindBash()
    {
        foreach (string candidate in new[] { "/bin/bash", "/usr/bin/bash" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "bash не найден: поведенческие тесты review.sh не выполнить (Linux — apt install bash, " +
            "Windows — Git Bash).");
    }
}
