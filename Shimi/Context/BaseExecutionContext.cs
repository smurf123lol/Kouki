using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Shimi.Context;

/// <summary>
/// Базовая реализация <see cref="IExecutionContext"/>: держит имя, три «железных» сервиса
/// и словарь дополнительных, а также (необязательно) провайдер DI.
/// </summary>
/// <example>
/// <code>
/// var context = new BaseExecutionContext("console", fileSystem, terminal, stateStore, services);
/// context.AddService&lt;IClock&gt;(new SystemClock());     // сервис, известный только контексту
/// IClock clock = context.GetRequiredService&lt;IClock&gt;("отметка времени в логе");
/// </code>
/// </example>
public class BaseExecutionContext : IExecutionContext
{
    private readonly Dictionary<Type, object> _services;

    /// <summary>
    /// Создаёт контекст.
    /// </summary>
    /// <param name="name">Имя контекста.</param>
    /// <param name="fileSystem">Файловая система окружения.</param>
    /// <param name="terminal">Терминал окружения.</param>
    /// <param name="stateStore">Хранилище состояния окружения.</param>
    /// <param name="services">
    /// Провайдер DI для остальных сервисов; можно не передавать, если контекст ни от чего не зависит.
    /// </param>
    public BaseExecutionContext(
        string name,
        IFileSystem fileSystem,
        ITerminal terminal,
        IStateStore stateStore,
        IServiceProvider? services = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        StateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        Services = services;

        _services = new Dictionary<Type, object>
        {
            [typeof(IFileSystem)] = FileSystem,
            [typeof(ITerminal)] = Terminal,
            [typeof(IStateStore)] = StateStore
        };
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IFileSystem FileSystem { get; }

    /// <inheritdoc />
    public ITerminal Terminal { get; }

    /// <inheritdoc />
    public IStateStore StateStore { get; }

    /// <inheritdoc />
    public IServiceProvider? Services { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<Type> ServiceTypes => _services.Keys;

    /// <summary>
    /// Добавляет сервис, который нужен только этому контексту (например, Id текущей сессии).
    /// </summary>
    /// <typeparam name="T">Тип, по которому сервис будет находиться.</typeparam>
    /// <param name="service">Экземпляр сервиса.</param>
    /// <returns>Этот же контекст — для цепочки вызовов.</returns>
    public BaseExecutionContext AddService<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        _services[typeof(T)] = service;
        return this;
    }

    /// <summary>
    /// Убирает добавленный ранее сервис. «Железные» сервисы (<see cref="FileSystem"/>,
    /// <see cref="Terminal"/>, <see cref="StateStore"/>) убрать нельзя — это часть контракта.
    /// </summary>
    /// <typeparam name="T">Тип сервиса.</typeparam>
    /// <returns>Удалён ли сервис.</returns>
    public bool RemoveService<T>() where T : class
        => typeof(T) != typeof(IFileSystem)
           && typeof(T) != typeof(ITerminal)
           && typeof(T) != typeof(IStateStore)
           && _services.Remove(typeof(T));

    /// <inheritdoc />
    public T? GetService<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out object? known))
        {
            return known as T;
        }

        return Services?.GetService<T>();
    }
}
