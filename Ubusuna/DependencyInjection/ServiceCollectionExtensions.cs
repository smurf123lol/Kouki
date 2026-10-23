using Kouki.Shimi.ServiceAbstractions;
using Kouki.Ubusuna.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kouki.Ubusuna.DependencyInjection;

/// <summary>Регистрация Ubusuna в <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует адаптеры Ubusuna (по умолчанию консольные).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Использует <c>TryAddSingleton</c>, чтобы приложение могло заранее подменить адаптеры своими
    /// реализациями.
    /// </para>
    /// <para>
    /// Контексты и контекст по умолчанию этот метод не создаёт: для этого используйте
    /// <see cref="UbusunaBuilder"/> либо <c>AddShimiContext()</c> + <c>SetDefaultContext</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddUbusuna(
        this IServiceCollection services,
        Action<UbusunaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new UbusunaOptions();
        configure?.Invoke(options);

        if (options.UseConsoleAdapters)
        {
            services.TryAddSingleton<IFileSystem, ConsoleFileSystem>();
            services.TryAddSingleton<ITerminal, ConsoleTerminal>();
        }

        return services;
    }
}

/// <summary>Параметры подключения Ubusuna.</summary>
public class UbusunaOptions
{
    /// <summary>Регистрировать консольные адаптеры (включено по умолчанию).</summary>
    public bool UseConsoleAdapters { get; set; } = true;
}
