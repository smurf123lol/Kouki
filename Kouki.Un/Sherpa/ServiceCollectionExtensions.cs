using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kouki.Un.Sherpa;

/// <summary>
/// Регистрация настоящего распознавания речи вместо заглушки.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Подключает <see cref="SherpaSpeechTranscriber"/> и убирает заглушку,
    /// зарегистрированную <c>AddUn()</c>.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configure">Настройки: каталог модели, язык, размерность признаков.</param>
    /// <returns>Та же коллекция — для цепочки вызовов.</returns>
    /// <remarks>
    /// Требует сборки с <c>-p:KoukiEnableSherpa=true</c>: без пакета
    /// <c>org.k2fsa.sherpa.onnx</c> этот файл не компилируется.
    /// </remarks>
    public static IServiceCollection AddUnSherpa(
        this IServiceCollection services,
        Action<SherpaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SherpaOptions();
        configure?.Invoke(options);

        services.RemoveAll<ISpeechTranscriber>();
        services.AddSingleton(options);
        services.AddSingleton<ISpeechTranscriber>(sp => new SherpaSpeechTranscriber(
            sp.GetRequiredService<SherpaOptions>()));

        return services;
    }
}
