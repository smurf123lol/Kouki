using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kotae;

/// <summary>Регистрация ответчика в контейнере.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Подключает ответчик: <see cref="IAnswerer"/> — рабочая модель, если провайдер
    /// задан и настройки сходятся, иначе <see cref="DisabledAnswerer"/> с причиной.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configure">Настройки (ключ, модель, таймаут).</param>
    /// <returns>Та же коллекция для цепочки вызовов.</returns>
    public static IServiceCollection AddKotae(this IServiceCollection services, Action<KotaeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new KotaeOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);

        services.TryAddSingleton<IAnswerer>(sp =>
        {
            var settings = sp.GetRequiredService<KotaeOptions>();
            var problem = settings.Validate();
            if (problem is not null)
            {
                return new DisabledAnswerer(problem);
            }

            return settings.Provider switch
            {
                KotaeProvider.Gemini => new GeminiAnswerer(settings),
                KotaeProvider.OpenAi => new OpenAiChatAnswerer(settings),
                _ => new DisabledAnswerer($"провайдер выключен: {settings.Provider}"),
            };
        });

        return services;
    }
}
