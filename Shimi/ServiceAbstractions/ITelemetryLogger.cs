namespace Shimi.ServiceAbstractions;

public interface ITelemetryLogger
{
    void LogEvent(string name, object? data = null);
    void LogError(string message, Exception? ex = null);
}