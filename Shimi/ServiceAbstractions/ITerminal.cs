namespace Shimi.ServiceAbstractions;

/// <summary>
/// TODO
/// </summary>
public interface ITerminal
{
    Task<string> RunCommandAsync(string command, string? workingDirectory = null, CancellationToken ct = default);
}
