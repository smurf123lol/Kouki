using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Kouki.Kagedama.Contracts;

namespace Kouki.Yasabi;

/// <summary>
/// Регистрация «Ясаби» в контейнере.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Подключает понимание текста: <see cref="IIntentClassifier"/> (с журналированием,
    /// если зарегистрирован <see cref="IClassificationJournal"/>) и <see cref="IIntentRouter"/>.
    /// Требует, чтобы до этого был вызван <c>AddMugura</c>: нужны реестр команд и маршрутизатор.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configure">Настройка правил и порога.</param>
    /// <returns>Та же коллекция для цепочки вызовов.</returns>
    public static IServiceCollection AddYasabi(this IServiceCollection services, Action<YasabiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new YasabiOptions();
        configure?.Invoke(options);

        if (options.Rules.Count == 0)
        {
            options.Rules.AddRange(RuleIntentClassifier.DefaultRules);
        }

        services.TryAddSingleton(options);

        services.TryAddSingleton(sp => new RuleIntentClassifier(sp.GetRequiredService<YasabiOptions>().Rules));

        services.TryAddSingleton<IIntentClassifier>(sp =>
        {
            var inner = sp.GetRequiredService<RuleIntentClassifier>();
            IClassificationJournal journal = sp.GetService<IClassificationJournal>() ?? NullClassificationJournal.Instance;

            return new LoggingIntentClassifier(inner, journal, sp.GetRequiredService<YasabiOptions>());
        });

        services.TryAddSingleton<IIntentRouter, IntentRouter>();

        // Каталог для сравнения роутеров на одной фразе: только настоящие движки.
        // Журналирующая обёртка — не отдельный движок: она классифицирует теми же правилами,
        // а побочно пишет запись в журнал, из-за чего сравнение ещё и портило датасет.
        // Будущая BERT-модель добавляется сюда же одной строкой, дашборд её увидит сам.
        services.TryAddSingleton<IClassifierCatalog>(sp => new ClassifierCatalog()
            .Add("rules", sp.GetRequiredService<RuleIntentClassifier>()));

        return services;
    }
}
