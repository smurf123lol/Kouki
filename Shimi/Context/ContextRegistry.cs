namespace Shimi.Context;

public class ContextRegistry : IContextRegistry
{
    private readonly Dictionary<string, IExecutionContext> _contexts = new();
    private IExecutionContext _defaultContext;

    public ContextRegistry()
    { }

    public IExecutionContext GetDefaultContext() => _defaultContext;

    public void SetDefaultContext(IExecutionContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        _defaultContext = context;
        // Обновляем запись в реестре
        _contexts["default"] = context;
    }

    public IExecutionContext GetContext(string name)
    {
        if (string.IsNullOrEmpty(name)) return _defaultContext;
        return _contexts.TryGetValue(name, out var ctx) ? ctx : _defaultContext;
    }

    public void RegisterContext(string name, IExecutionContext context)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name cannot be null or empty.", nameof(name));
        _contexts[name] = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void SetDefaultContext(string context)
    {
        IExecutionContext c = null;
        if (_contexts.TryGetValue(context, out c))
        {
            _defaultContext = c;
        }
        else {
            throw new ArgumentException("context '"+context+"' not exist");
        }
    }
}