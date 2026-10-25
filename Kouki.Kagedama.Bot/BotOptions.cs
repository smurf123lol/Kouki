using Microsoft.Extensions.Configuration;

namespace Kouki.Kagedama.Bot;

/// <summary>
/// Настройки бота: токен Telegram, адрес Kouki и режим опроса.
/// Берутся из <c>appsettings.json</c> (секция <c>KoukiBot</c>), переменных окружения
/// и аргументов командной строки — в этом порядке перекрытия.
/// </summary>
public sealed class BotOptions
{
    /// <summary>Секция конфигурации с настройками бота.</summary>
    public const string SectionName = "KoukiBot";

    /// <summary>Токен бота Telegram.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Базовый адрес Bot API. Меняется только для тестов: подменяем на локальный сервер.
    /// </summary>
    public string TelegramApiUrl { get; set; } = "https://api.telegram.org";

    /// <summary>Адрес сервера Kouki.</summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:5199";

    /// <summary>Bearer-токен сервера; пустая строка — без проверки.</summary>
    public string ServerToken { get; set; } = string.Empty;

    /// <summary>Источник, которым бот подписывает задачи.</summary>
    public string Source { get; set; } = "telegram";

    /// <summary>Длительность long polling в секундах.</summary>
    public int PollTimeoutSeconds { get; set; } = 25;

    /// <summary>
    /// Собирает настройки из файла, окружения и аргументов.
    /// </summary>
    /// <param name="args">Аргументы вида <c>--bot-token=…</c>, <c>--server-url=…</c>, <c>--server-token=…</c>.</param>
    /// <param name="baseDirectory">
    /// Где искать <c>appsettings.json</c>. По умолчанию — каталог приложения; параметр нужен тестам.
    /// </param>
    /// <exception cref="BotConfigurationException">
    /// Файл настроек есть, но разобрать его не удалось. Молча подставлять значения по умолчанию
    /// нельзя: бот с чужим токеном и адресом сервера выглядит как «ничего не работает», и найти
    /// причину в таком виде почти невозможно.
    /// </exception>
    public static BotOptions Load(string[] args, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        string directory = baseDirectory ?? AppContext.BaseDirectory;
        string configPath = Path.Combine(directory, "appsettings.json");

        IConfigurationRoot configuration;
        try
        {
            // Абсолютный путь вместо SetBasePath: каталога может не быть вовсе
            // (тесты, ручной запуск из пустой папки), а это не ошибка — просто нет файла.
            configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: true, reloadOnChange: false)
                .Build();
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            // Внутри обычно лежит JsonException с номером строки и символом — именно он
            // и нужен человеку, а не «Failed to load configuration from file».
            Exception root = ex;
            while (root.InnerException is not null)
            {
                root = root.InnerException;
            }
            throw new BotConfigurationException(
                $"Не удалось прочитать {configPath}: {root.Message} " +
                "Проверьте JSON в секции KoukiBot или задайте настройки переменными окружения " +
                "(KOUKI_BOT_TOKEN, KOUKI_SERVER_URL) либо аргументами (--bot-token=…).",
                ex);
        }

        var options = new BotOptions();
        IConfigurationSection section = configuration.GetSection(SectionName);
        options.BotToken = section[nameof(BotToken)] ?? options.BotToken;
        options.TelegramApiUrl = section[nameof(TelegramApiUrl)] ?? options.TelegramApiUrl;
        options.ServerUrl = section[nameof(ServerUrl)] ?? options.ServerUrl;
        options.ServerToken = section[nameof(ServerToken)] ?? options.ServerToken;
        options.Source = section[nameof(Source)] ?? options.Source;
        if (int.TryParse(section[nameof(PollTimeoutSeconds)], out int configuredTimeout))
        {
            options.PollTimeoutSeconds = configuredTimeout;
        }

        // Переменные окружения читаем явно: у них свои имена (KOUKI_*), а не KoukiBot:*.
        options.BotToken = Environment.GetEnvironmentVariable("KOUKI_BOT_TOKEN") ?? options.BotToken;
        options.ServerUrl = Environment.GetEnvironmentVariable("KOUKI_SERVER_URL") ?? options.ServerUrl;
        options.TelegramApiUrl = Environment.GetEnvironmentVariable("KOUKI_TELEGRAM_API_URL") ?? options.TelegramApiUrl;
        options.ServerToken = Environment.GetEnvironmentVariable("KOUKI_SERVER_TOKEN") ?? options.ServerToken;
        options.Source = Environment.GetEnvironmentVariable("KOUKI_SOURCE") ?? options.Source;

        ApplyArguments(options, args);

        if (options.PollTimeoutSeconds < 1)
        {
            options.PollTimeoutSeconds = 1;
        }

        return options;
    }

    /// <summary>
    /// Перекрывает настройки аргументами командной строки.
    /// </summary>
    private static void ApplyArguments(BotOptions options, string[] args)
    {
        foreach (string arg in args)
        {
            int separator = arg.IndexOf('=');
            if (separator <= 0 || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string name = arg[2..separator];
            string value = arg[(separator + 1)..];

            switch (name)
            {
                case "bot-token":
                    options.BotToken = value;
                    break;
                case "server-url":
                    options.ServerUrl = value;
                    break;
                case "server-token":
                    options.ServerToken = value;
                    break;
                case "source":
                    options.Source = value;
                    break;
                case "telegram-api-url":
                    options.TelegramApiUrl = value;
                    break;
                case "poll-timeout":
                    if (int.TryParse(value, out int timeout))
                    {
                        options.PollTimeoutSeconds = timeout;
                    }

                    break;
            }
        }
    }
}
