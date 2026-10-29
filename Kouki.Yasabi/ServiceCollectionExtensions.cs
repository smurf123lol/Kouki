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
        services.TryAddSingleton(options.Llm);

        services.TryAddSingleton(sp => new RuleIntentClassifier(sp.GetRequiredService<YasabiOptions>().Rules));

        services.TryAddSingleton<IIntentClassifier>(sp =>
        {
            var intentOptions = sp.GetRequiredService<YasabiOptions>();
            IClassificationJournal journal = sp.GetService<IClassificationJournal>() ?? NullClassificationJournal.Instance;

            // Модель в приёме — осознанный выбор: правила быстрее, модель понимает больше
            // формулировок. Пока не включено, приём идёт через правила.
            IIntentClassifier inner = intentOptions.Llm is { Enabled: true, UseForIntake: true }
                ? new LlmIntentClassifier(new HttpClient(), intentOptions.Llm)
                : sp.GetRequiredService<RuleIntentClassifier>();

            return new LoggingIntentClassifier(inner, journal, intentOptions);
        });

        services.TryAddSingleton<IIntentRouter, IntentRouter>();

        // Каталог для сравнения роутеров на одной фразе: только настоящие движки.
        // Журналирующая обёртка — не отдельный движок: она классифицирует теми же правилами,
        // а побочно пишет запись в журнал, из-за чего сравнение ещё и портило датасет.
        // Модель попадает в дашборд сама: он читает каталог с сервера.
        services.TryAddSingleton<IClassifierCatalog>(sp =>
        {
            var catalog = new ClassifierCatalog().Add("rules", sp.GetRequiredService<RuleIntentClassifier>());
            var llm = sp.GetRequiredService<LlmIntentOptions>();

            if (llm.Enabled)
            {
                catalog.Add(LlmIntentClassifier.ModelName, new LlmIntentClassifier(new HttpClient(), llm));
            }

            return catalog;
        });

        return services;
    }
}
