using Kouki.Shimi.Context;
using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kouki.Shimi.DependencyInjection;

/// <summary>Регистрация базовых сервисов Shimi.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует контекстный реестр и хранилище состояния в памяти.
    /// </summary>
    /// <remarks>
    /// Обе регистрации — <c>TryAdd</c>: приложение может объявить свои реализации
    /// (например, персистентное <see cref="IStateStore"/>) до вызова этого метода.
    /// </remarks>
    public static IServiceCollection AddShimiContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IContextRegistry, ContextRegistry>();
        services.TryAddSingleton<IStateStore, InMemoryStateStore>();

        return services;
    }
}
