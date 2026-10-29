using System.Reflection;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Kotae;
using Kouki.Yasabi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kouki.Kagedama.Hosting;

/// <summary>
/// Настройки хоста Kouki: то, что раньше каждый хост задавал у себя по-своему.
/// </summary>
public sealed record KoukiHostSettings
{
    /// <summary>Имя секции настроек в конфигурации хоста.</summary>
    public const string SectionName = "Kouki";

    /// <summary>Путь к файлу SQLite.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Порог уверенности распознавания; ниже него фраза уходит на разбор.</summary>
    public double ConfidenceThreshold { get; init; } = IntakeOptions.DefaultConfidenceThreshold;

    /// <summary>Источник фраз, если запрос его не указал, см. <see cref="TaskSources"/>.</summary>
    public string DefaultSource { get; init; } = TaskSources.Api;

    /// <summary>Сборки, где искать команды; по умолчанию — сборка домена.</summary>
    public IReadOnlyList<Assembly> CommandAssemblies { get; init; } = new[] { typeof(AddTaskHandler).Assembly };

    /// <summary>Ответчик на вопросы; <c>null</c> или выключенный провайдер — команда <c>ask</c> отвечает заглушкой.</summary>
    public KotaeOptions? Answerer { get; init; }

    /// <summary>
    /// Классификатор интентов на языковой модели; <c>null</c> или <c>Enabled = false</c> —
    /// интенты определяют правила, модель даже не попадает в сравнение.
    /// </summary>
    public LlmIntentOptions? IntentLlm { get; init; }

    /// <summary>
    /// Часовой пояс человека для напоминаний (IANA или имя Windows).
    /// <c>null</c> — переменная окружения <c>KOUKI_TIMEZONE</c>, а если её нет — пояс сервера.
    /// </summary>
    public string? TimeZone { get; init; }
}

/// <summary>
/// Общая композиция хостов (api, MCP, бот в будущем): одна цепочка регистраций,
/// чтобы хосты не расходились в том, какие команды и сервисы у них есть.
/// </summary>
public static class KoukiHosting
{
    /// <summary>
    /// Путь к базе по умолчанию — рядом с бинарником, чтобы api и MCP,
    /// запущенные «из коробки», работали с одним файлом.
    /// </summary>
    public static string DefaultDatabasePath()
        => Path.Combine(AppContext.BaseDirectory, "kouki.sqlite");

    /// <summary>
    /// Регистрирует домен: хранилище, команды, контексты, классификатор, приём.
    /// Голос, MCP-транспорт и прочие надстройки хост добавляет сам.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="settings">Настройки хоста.</param>
    public static IServiceCollection AddKoukiCore(this IServiceCollection services, KoukiHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.DatabasePath);

        Assembly[] assemblies = settings.CommandAssemblies.Count == 0
            ? new[] { typeof(AddTaskHandler).Assembly }
            : settings.CommandAssemblies.ToArray();

        services.AddSingleton(settings);
        services.AddKagedamaData(settings.DatabasePath);
        services.AddMugura(assemblies);
        services.AddShimiContext();
        services.AddYasabi(options =>
        {
            options.ConfidenceThreshold = settings.ConfidenceThreshold;
            options.Source = settings.DefaultSource;

            if (settings.IntentLlm is { } llm)
            {
                options.Llm = llm;
            }
        });
        services.AddSingleton(new IntakeOptions
        {
            ConfidenceThreshold = settings.ConfidenceThreshold,
            DefaultSource = settings.DefaultSource,
        });
        services.AddSingleton<TaskIntakeService>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => ReminderTimeZones.Resolve(settings.TimeZone));

        // Ответчик подключается всегда: без ключа это DisabledAnswerer, и в
        // диагностике видно причину, а команда ask отвечает заглушкой.
        services.AddKotae(options =>
        {
            if (settings.Answerer is not { } answerer)
            {
                return;
            }

            options.Provider = answerer.Provider;
            options.ApiKey = answerer.ApiKey;
            options.Model = answerer.Model;
            options.Endpoint = answerer.Endpoint;
            options.TimeoutSeconds = answerer.TimeoutSeconds;
            options.MaxOutputTokens = answerer.MaxOutputTokens;
            options.SystemPrompt = answerer.SystemPrompt;
        });

        return services;
    }

    /// <summary>
    /// Готовит базу и контекст исполнения. Идемпотентно: повторный вызов не перезапишет
    /// уже установленный контекст по умолчанию.
    /// </summary>
    /// <param name="provider">Провайдер сервисов.</param>
    /// <param name="contextName">Имя контекста: server, mcp и т.п.</param>
    /// <returns>Путь к инициализированной базе.</returns>
    public static string InitializeKouki(IServiceProvider provider, string contextName)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);

        KoukiHostSettings settings = provider.GetRequiredService<KoukiHostSettings>();
        string databasePath = DatabaseInitializer.Initialize(settings.DatabasePath);

        IContextRegistry registry = provider.GetRequiredService<IContextRegistry>();

        if (registry.GetDefaultContext() is null)
        {
            registry.SetDefaultContext(new BaseExecutionContext(
                contextName,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<ITerminal>(),
                provider.GetRequiredService<IStateStore>(),
                provider));
        }

        return databasePath;
    }
}
