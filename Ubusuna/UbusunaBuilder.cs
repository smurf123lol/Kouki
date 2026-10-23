using Kouki.Shimi.Context;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Ubusuna.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kouki.Ubusuna;

/// <summary>
/// Fluent-конструктор слоя Ubusuna: регистрирует адаптеры (файловая система, терминал),
/// собирает <see cref="IContextRegistry"/> из объявленных контекстов и назначает контекст
/// по умолчанию.
/// </summary>
/// <remarks>
/// <para>
/// Реестр контекстов создаётся и заполняется ровно один раз — фабрикой singleton-регистрации,
/// которой передаётся реальный <see cref="IServiceProvider"/> контейнера. Второй контейнер
/// не создаётся, поэтому singleton-адаптеры не дублируются.
/// </para>
/// <para>
/// Регистрация выполняется последней (через <see cref="IServiceCollection"/> пользователя),
/// то есть перекрывает предыдущую регистрацию <see cref="IContextRegistry"/>, если она была
/// (например, из <c>AddShimiContext</c>). Если нужен свой реестр — зарегистрируйте его после
/// вызова <see cref="Build"/>.
/// </para>
/// </remarks>
public class UbusunaBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Action<IServiceCollection>> _configurators = new();
    private readonly List<(string Name, Func<IServiceProvider, IExecutionContext> Factory)> _contexts = new();
    private string _defaultContextName = "default";
    private bool _useConsoleAdapters = true;
    private bool _built;

    public UbusunaBuilder(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>Регистрировать ли консольные адаптеры по умолчанию (включено).</summary>
    public UbusunaBuilder UseConsoleAdapters(bool use = true)
    {
        _useConsoleAdapters = use;
        return this;
    }

    /// <summary>Объявить контекст; фабрика получает реальный <see cref="IServiceProvider"/>.</summary>
    public UbusunaBuilder AddContext(string name, Func<IServiceProvider, IExecutionContext> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        _contexts.Add((name, factory));
        return this;
    }

    /// <summary>Имя контекста, который станет контекстом по умолчанию (по умолчанию <c>"default"</c>).</summary>
    public UbusunaBuilder SetDefaultContext(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _defaultContextName = name;
        return this;
    }

    /// <summary>Дополнительная настройка <see cref="IServiceCollection"/> перед регистрацией Ubusuna.</summary>
    public UbusunaBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configurators.Add(configure);
        return this;
    }

    /// <summary>Применить накопленную конфигурацию к <see cref="IServiceCollection"/>.</summary>
    public void Build()
    {
        if (_built)
            throw new InvalidOperationException(
                "UbusunaBuilder.Build() уже вызывался: повторный вызов продублировал бы регистрации.");

        _built = true;

        foreach (var configure in _configurators)
            configure(_services);

        if (_useConsoleAdapters)
        {
            _services.TryAddSingleton<IFileSystem, ConsoleFileSystem>();
            _services.TryAddSingleton<ITerminal, ConsoleTerminal>();
        }

        _services.AddSingleton<IContextRegistry>(CreateRegistry);
    }

    private IContextRegistry CreateRegistry(IServiceProvider services)
    {
        var registry = new ContextRegistry();

        foreach (var (name, factory) in _contexts)
        {
            var context = factory(services)
                ?? throw new InvalidOperationException($"Фабрика контекста '{name}' вернула null.");
            registry.RegisterContext(name, context);
        }

        // SetDefaultContext(string) бросает, если имя не зарегистрировано,
        // поэтому проверяем наличие контекста сами и при необходимости создаём консольный.
        if (_contexts.Any(context => context.Name == _defaultContextName))
        {
            registry.SetDefaultContext(_defaultContextName);
        }
        else
        {
            registry.RegisterContext(_defaultContextName, CreateConsoleContext(services, _defaultContextName));
            registry.SetDefaultContext(_defaultContextName);
        }

        return registry;
    }

    private static IExecutionContext CreateConsoleContext(IServiceProvider services, string name)
        => new BaseExecutionContext(
            name,
            services.GetService<IFileSystem>() ?? new ConsoleFileSystem(),
            services.GetService<ITerminal>() ?? new ConsoleTerminal(),
            services.GetService<IStateStore>() ?? new InMemoryStateStore());
}
