using Kouki.Ubusuna.Adapters;

namespace Kouki.Tests;

/// <summary>Проверки консольного терминала (аргументы не склеиваются в строку).</summary>
public sealed class ConsoleTerminalTests
{
    [Fact]
    public async Task Runs_command_and_returns_stdout()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var terminal = new ConsoleTerminal();

        var output = await terminal.RunCommandAsync("echo hello-from-terminal");

        Assert.Contains("hello-from-terminal", output);
    }

    [Fact]
    public async Task Failing_command_reports_exit_code_and_stderr()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var terminal = new ConsoleTerminal();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => terminal.RunCommandAsync("exit /b 7"));

        Assert.Contains("7", exception.Message);
    }
}
