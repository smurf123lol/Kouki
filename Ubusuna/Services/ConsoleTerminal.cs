// Kouki.Ubusuna/Adapters/ConsoleTerminal.cs
using Shimi.ServiceAbstractions;
using System.Diagnostics;

namespace Kouki.Ubusuna.Adapters;

public class ConsoleTerminal : ITerminal
{
    public async Task<string> RunCommandAsync(string command, string? workingDirectory = null, CancellationToken ct = default)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"Command failed: {error}");

        return output;
    }
}