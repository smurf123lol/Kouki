using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Kouki.Shimi.Context;

/// <summary>
/// Потокобезопасный реестр контекстов: словарь на <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// указатель на контекст по умолчанию — через <see cref="Volatile"/>.
/// </summary>
public sealed class ContextRegistry : IContextRegistry
{
    private readonly ConcurrentDictionary<string, IExecutionContext> _contexts =
        new(StringComparer.Ordinal);

    private IExecutionContext? _defaultContext;

    public ContextRegistry()
    {
    }

    public IExecutionContext? GetDefaultContext() => Volatile.Read(ref _defaultContext);

    public bool TryGetDefaultContext([NotNullWhen(true)] out IExecutionContext? context)
    {
        context = Volatile.Read(ref _defaultContext);
        return context is not null;
    }

    public void SetDefaultContext(IExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Volatile.Write(ref _defaultContext, context);
        // Держим запись "default" в реестре, чтобы GetContext("default") работал
        // без отдельной ветки.
        _contexts["default"] = context;
    }

    public IExecutionContext? GetContext(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return GetDefaultContext();
        }

        return _contexts.TryGetValue(name, out var context) ? context : GetDefaultContext();
    }

    public void RegisterContext(string name, IExecutionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(context);
        _contexts[name] = context;
    }

    public void SetDefaultContext(string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        if (!_contexts.TryGetValue(context, out var registered))
        {
            throw new ArgumentException($"Context '{context}' is not registered.", nameof(context));
        }

        Volatile.Write(ref _defaultContext, registered);
    }
}
