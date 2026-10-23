using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kouki.Un;

/// <summary>
/// Регистрация слуха «Ун».
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="ISpeechTranscriber"/>. Сейчас это заглушка
    /// <see cref="UnavailableSpeechTranscriber"/>; на этапе M4 её сменит sherpa-onnx.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <returns>Та же коллекция для цепочки вызовов.</returns>
    public static IServiceCollection AddUn(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISpeechTranscriber, UnavailableSpeechTranscriber>();
        return services;
    }
}
