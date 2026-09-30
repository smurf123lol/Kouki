using Shimi.ServiceAbstractions;

namespace Shimi.Context;

public class BaseExecutionContext : IExecutionContext
{
    public string Name { get; }
    public IFileSystem FileSystem { get; }
    public ITerminal Terminal { get; }
    public IStateStore StateStore { get; }
    private readonly Dictionary<Type, object> _services;

    public BaseExecutionContext(
        string name,
        IFileSystem fileSystem,
        ITerminal terminal,
        IStateStore stateStore)
    {
        Name = name;
        FileSystem = fileSystem;
        Terminal = terminal;
        StateStore = stateStore;
        _services = new Dictionary<Type, object>
        {
            [typeof(IFileSystem)] = fileSystem,
            [typeof(ITerminal)] = terminal,
            [typeof(IStateStore)] = stateStore
        };
    }

    public T? GetService<T>() where T : class
    {
        return _services.TryGetValue(typeof(T), out var service) ? service as T : null;
    }
}