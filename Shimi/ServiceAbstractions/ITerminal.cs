namespace Kouki.Shimi.ServiceAbstractions;

/// <summary>
/// Запуск внешних процессов: возвращает stdout, при ненулевом коде выхода — ошибка.
/// </summary>
public interface ITerminal
{
    Task<string> RunCommandAsync(string command, string? workingDirectory = null, CancellationToken ct = default);
}
