using System.Reflection;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Kagedama.Hosting;
using Kouki.Kagedama.Mcp;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Un;
using Kouki.Ubusuna;
using Kouki.Ubusuna.DependencyInjection;
using Kouki.Yasabi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// MCP-сервер Kouki: транспорт stdio. В stdout попадают только JSON-RPC ответы,
// поэтому весь лог уходит в stderr.
string? databaseArgument = FindArgument(args, "--database");
string? thresholdArgument = FindArgument(args, "--threshold");

// Настройки как у api: файл appsettings.json рядом с приложением, поверх него
// переменные окружения (Kouki__DatabasePath), поверх — аргументы командной строки.
// Раньше файл лежал в проекте, но не читался: это был мёртвый конфиг.
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

string section = KoukiHostSettings.SectionName;

// Путь к базе: аргумент, затем переменные окружения (своя и стиль конфигурации хоста),
// затем appsettings.json. Относительный путь разрешается от каталога приложения,
// а не от текущего — иначе mcp и api смотрели бы в разные базы.
string databasePath = KoukiPaths.Resolve(
    databaseArgument
        ?? Environment.GetEnvironmentVariable("KOUKI_DATABASE")
        ?? configuration[$"{section}:DatabasePath"],
    "kouki.sqlite",
    "DatabasePath");

double threshold = double.TryParse(
        thresholdArgument ?? Environment.GetEnvironmentVariable("KOUKI_THRESHOLD"),
        out double parsedThreshold)
    ? parsedThreshold
    : double.TryParse(configuration[$"{section}:ConfidenceThreshold"], out double configuredThreshold)
        ? configuredThreshold
        : IntakeOptions.DefaultConfidenceThreshold;

var services = new ServiceCollection();

services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });
});

// Хранилище, команды и контекст собираются общим слоем: MCP и API в одном процессе
// работают одинаково, различается только транспорт и источник.
services.AddKoukiCore(new KoukiHostSettings
{
    DatabasePath = databasePath,
    ConfidenceThreshold = threshold,
    DefaultSource = TaskSources.Mcp,
    CommandAssemblies =
    [
        typeof(Kouki.Ubusuna.Commands.PingHandler).Assembly,
        typeof(Kouki.Kagedama.Commands.ListTasksHandler).Assembly,
    ],
});

services.AddUbusuna();
services.AddUn();
services.AddSingleton<KoukiMcpTools>();
services.AddSingleton<McpSession>();

using ServiceProvider provider = services.BuildServiceProvider();

ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Kouki.Mcp");
logger.LogInformation("Kouki MCP: база данных {Path}", KoukiHosting.InitializeKouki(provider, "mcp"));

McpSession session = provider.GetRequiredService<McpSession>();
logger.LogInformation("Kouki MCP: сервер готов, жду JSON-RPC в stdin");

// Спецификация MCP: stdio — это UTF-8 без BOM и по одному сообщению в строке.
var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };

await session.RunAsync(stdin, stdout, CancellationToken.None);

static string? FindArgument(string[] arguments, string name)
{
    string prefix = name + "=";

    foreach (string argument in arguments)
    {
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return argument[prefix.Length..];
        }
    }

    return null;
}
