using Kouki.Mugura.Dispatcher;
using Kouki.Ubusuna.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Shimi.ServiceAbstractions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUbusuna(this IServiceCollection services, Action<UbusunaOptions>? configure = null)
    {
        var options = new UbusunaOptions();
        configure?.Invoke(options);

        // Регистрация адаптеров по умолчанию, если включены
        if (options.UseConsoleAdapters)
        {
            services.AddSingleton<IFileSystem, ConsoleFileSystem>();
            services.AddSingleton<ITerminal, ConsoleTerminal>();
        }

        // Можно зарегистрировать контекст по умолчанию, но не устанавливать его как default
        // (это делает приложение через SetDefaultContext)

        return services;
    }
}

public class UbusunaOptions
{
    public bool UseConsoleAdapters { get; set; } = true;
}