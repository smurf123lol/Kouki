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
    /// Приём идёт связкой «правила + модель»: правила отвечают первыми, модель — на неуверенных фразах.
    /// </summary>
    public bool HybridIntents { get; init; } = true;

    /// <summary>Порог уверенности правил, ниже которого приём спрашивает модель.</summary>
    public double HybridFallbackBelow { get; init; } = 0.95d;

    /// <summary>
    /// Часовой пояс человека для напоминаний (IANA или имя Windows).
    /// <c>null</c> — переменная окружения <c>KOUKI_TIMEZONE</c>, а если её нет — пояс сервера.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Каталог с моделью классификации интентов RuBERT (rubert-tiny2-intents.onnx, vocab.txt).
    /// </summary>
    public string? IntentModelDirectory { get; init; }

    /// <summary>
    /// Режим тегов по умолчанию, см. <see cref="TagModes"/>. Неизвестное значение — ошибка запуска.
    /// </summary>
    public string TagMode { get; init; } = TagModes.Extract;

    /// <summary>
    /// Режим тегов на конкретный источник: ключ — <see cref="TaskSources"/>, значение — <see cref="TagModes"/>.
    /// Источник без переопределения берёт <see cref="TagMode"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> TagModeBySource { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Что можно написать в настройке режима тегов.</summary>
    private const string AllowedTagModes = "extract — вырезать теги из фразы, keep — оставить # в тексте";

    /// <summary>
    /// Проверяет режимы тегов до регистраций.
    /// </summary>
    /// <remarks>
    /// Fail-closed: опечатка в <c>Kouki:TagMode</c> (например, <c>extarct</c>) или неизвестный
    /// источник в <c>Kouki:TagModeBySource</c> — это ошибка запуска, а не «тихий keep»:
    /// иначе сервер поднимется с настройкой, которую никто не имел в виду, и будет молча
    /// складывать теги в текст задачи.
    /// </remarks>
    /// <param name="settings">Настройки хоста.</param>
    /// <exception cref="InvalidOperationException">Режим или источник неизвестен.</exception>
    private static void ValidateTagModes(KoukiHostSettings settings)
    {
        if (!TagModes.IsKnown(settings.TagMode))
        {
            throw new InvalidOperationException(TagModeError(null, settings.TagMode));
        }

        foreach (KeyValuePair<string, string> pair in settings.TagModeBySource)
        {
            if (TaskSources.IsKnown(pair.Key) && TagModes.IsKnown(pair.Value))
            {
                continue;
            }

            throw new InvalidOperationException(TagModeError(pair.Key, pair.Value));
        }
    }

    private static string TagModeError(string? source, string? mode) => source is null
        ? $"Неизвестный режим тегов '{mode}' в Kouki:TagMode[BySource]. Допустимые режимы: {AllowedTagModes}."
        : $"Неизвестный режим тегов '{mode}' (или источник '{source}') в Kouki:TagMode[BySource]. Допустимые режимы: {AllowedTagModes}.";

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
        ValidateTagModes(settings);

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

            options.HybridForIntake = settings.HybridIntents;
            options.HybridFallbackBelow = settings.HybridFallbackBelow;

            if (!string.IsNullOrWhiteSpace(settings.IntentModelDirectory))
            {
                string modelPath = Path.Combine(settings.IntentModelDirectory, "rubert-tiny2-intents.onnx");
                string vocabPath = Path.Combine(settings.IntentModelDirectory, "vocab.txt");
                if (File.Exists(modelPath) && File.Exists(vocabPath))
                {
                    options.OnnxModelPath = modelPath;
                    options.OnnxVocabPath = vocabPath;
                }
            }
        });
        services.AddSingleton(new IntakeOptions
        {
            ConfidenceThreshold = settings.ConfidenceThreshold,
            DefaultSource = settings.DefaultSource,
            TagMode = settings.TagMode,
            TagModeBySource = settings.TagModeBySource,
            AsyncModelTagging = true,
        });
        services.AddSingleton<TaskIntakeService>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => ReminderTimeZones.Resolve(settings.TimeZone));
        services.TryAddSingleton<ITaskTitleGenerator>(sp =>
        {
            if (settings.IntentLlm is { Enabled: true } llm)
            {
                return new LlmTaskTitleGenerator(new HttpClient(), llm.Endpoint, llm.Model, RuleTaskTitleGenerator.Instance);
            }

            return RuleTaskTitleGenerator.Instance;
        });

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
