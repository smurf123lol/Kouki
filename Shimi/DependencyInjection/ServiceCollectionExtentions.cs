using Microsoft.Extensions.DependencyInjection;

namespace Shimi.Context.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует базовые сервисы Shimi: контекстный реестр и хранилище в памяти.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <returns>IServiceCollection для цепочки вызовов.</returns>
    public static IServiceCollection AddShimiContext(this IServiceCollection services)
    {
        // Реестр контекстов — синглтон
        services.AddSingleton<IContextRegistry, ContextRegistry>();

        return services;
    }
}