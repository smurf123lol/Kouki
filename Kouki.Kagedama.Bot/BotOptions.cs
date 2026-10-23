using System.Text.Json;

namespace Kouki.Kagedama.Bot;

/// <summary>
/// Настройки бота: токен Telegram, адрес Kouki и режим опроса.
/// Берутся из <c>appsettings.json</c> (секция <c>KoukiBot</c>), переменных окружения
/// и аргументов командной строки — в этом порядке перекрытия.
/// </summary>
public sealed class BotOptions
{
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
    public static BotOptions Load(string[] args)
    {
        var options = new BotOptions();

        string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(configPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
                if (document.RootElement.TryGetProperty("KoukiBot", out JsonElement section))
                {
                    options.BotToken = ReadString(section, "BotToken") ?? options.BotToken;
                    options.ServerUrl = ReadString(section, "ServerUrl") ?? options.ServerUrl;
                    options.ServerToken = ReadString(section, "ServerToken") ?? options.ServerToken;
                    options.Source = ReadString(section, "Source") ?? options.Source;
        options.TelegramApiUrl = ReadString(section, "TelegramApiUrl") ?? options.TelegramApiUrl;
                    options.PollTimeoutSeconds = ReadInt(section, "PollTimeoutSeconds") ?? options.PollTimeoutSeconds;
                }
            }
            catch (JsonException)
            {
                // Битый конфиг не должен мешать запуску: дальше решат окружение и аргументы.
            }
        }

        options.BotToken = Environment.GetEnvironmentVariable("KOUKI_BOT_TOKEN") ?? options.BotToken;
        options.ServerUrl = Environment.GetEnvironmentVariable("KOUKI_SERVER_URL") ?? options.ServerUrl;
        options.TelegramApiUrl = Environment.GetEnvironmentVariable("KOUKI_TELEGRAM_API_URL") ?? options.TelegramApiUrl;
        options.ServerToken = Environment.GetEnvironmentVariable("KOUKI_SERVER_TOKEN") ?? options.ServerToken;

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
                default:
                    break;
            }
        }

        if (options.PollTimeoutSeconds < 1)
        {
            options.PollTimeoutSeconds = 1;
        }

        return options;
    }

    private static string? ReadString(JsonElement section, string name)
        => section.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement section, string name)
        => section.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : null;
}
