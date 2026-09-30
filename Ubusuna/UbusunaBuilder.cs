using Kouki.Ubusuna.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Shimi.Context;
using Shimi.ServiceAbstractions;

public class UbusunaBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Action<IServiceCollection>> _configurators = new();
    private readonly List<(string name, Func<IServiceProvider, IExecutionContext> factory)> _contexts = new();
    private string _defaultContextName = "default";
    private bool _useConsoleAdapters = true;

    public UbusunaBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public UbusunaBuilder UseConsoleAdapters(bool use = true)
    {
        _useConsoleAdapters = use;
        return this;
    }

    public UbusunaBuilder AddContext(string name, Func<IServiceProvider, IExecutionContext> factory)
    {
        _contexts.Add((name, factory));
        return this;
    }

    public UbusunaBuilder SetDefaultContext(string name)
    {
        _defaultContextName = name;
        return this;
    }

    public UbusunaBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _configurators.Add(configure);
        return this;
    }

    public void Build()
    {
        // Применяем конфигураторы
        foreach (var config in _configurators)
            config(_services);

        // Регистрируем адаптеры
        if (_useConsoleAdapters)
        {
            _services.AddSingleton<IFileSystem, ConsoleFileSystem>();
            _services.AddSingleton<ITerminal, ConsoleTerminal>();
        }

        // Регистрируем контексты через фабрику
        _services.AddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<IContextRegistry>();
            foreach (var (name, factory) in _contexts)
            {
                var context = factory(sp);
                registry.RegisterContext(name, context);
            }

            // Устанавливаем дефолтный контекст, если он ещё не установлен
            if (registry.GetDefaultContext() == null)
            {
                var defaultCtx = _contexts.FirstOrDefault(x => x.name == _defaultContextName);
                if (defaultCtx == default)
                {
                    // Создаём консольный контекст по умолчанию, если не задан
                    var fs = _services.BuildServiceProvider().GetService<IFileSystem>() ?? new ConsoleFileSystem();
                    var terminal = _services.BuildServiceProvider().GetService<ITerminal>() ?? new ConsoleTerminal();
                    var stateStore = new InMemoryStateStore();
                    var ctx = new BaseExecutionContext(_defaultContextName, fs, terminal, stateStore);
                    registry.SetDefaultContext(ctx);
                }
                else
                {
                    registry.SetDefaultContext(defaultCtx.factory(sp));
                }
            }

            return registry;
        });
    }
}