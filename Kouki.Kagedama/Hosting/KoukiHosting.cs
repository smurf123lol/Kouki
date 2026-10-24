using System.Reflection;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Yasabi;
using Microsoft.Extensions.DependencyInjection;

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
        });
        services.AddSingleton(new IntakeOptions
        {
            ConfidenceThreshold = settings.ConfidenceThreshold,
            DefaultSource = settings.DefaultSource,
        });
        services.AddSingleton<TaskIntakeService>();

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
