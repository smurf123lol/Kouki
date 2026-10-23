using Kouki.Shimi.ServiceAbstractions;
using System.Diagnostics;

namespace Kouki.Ubusuna.Adapters;

/// <summary>
/// Реализация <see cref="ITerminal"/> через системную оболочку.
/// </summary>
/// <remarks>
/// Команда передаётся через <see cref="ProcessStartInfo.ArgumentList"/>, а не через
/// склеенную строку <c>Arguments</c>: аргументы не нужно экранировать вручную.
/// На Windows используется <c>cmd.exe /c</c>, на Unix — <c>/bin/sh -c</c>.
/// </remarks>
public class ConsoleTerminal : ITerminal
{
    public async Task<string> RunCommandAsync(
        string command,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
        var standardError = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Не оставляем дочерний процесс висеть после отмены.
            TryKill(process);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command '{command}' exited with code {process.ExitCode}: {error}");

        return output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершился между проверкой и вызовом Kill.
        }
    }
}
