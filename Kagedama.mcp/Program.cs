using System.Reflection;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Kagedama.Mcp;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Un;
using Kouki.Ubusuna;
using Kouki.Ubusuna.DependencyInjection;
using Kouki.Yasabi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// MCP-сервер Kouki: транспорт stdio. В stdout попадают только JSON-RPC ответы,
// поэтому весь лог уходит в stderr.
string? databaseArgument = FindArgument(args, "--database");
string? thresholdArgument = FindArgument(args, "--threshold");

string databasePath = databaseArgument
    ?? Environment.GetEnvironmentVariable("KOUKI_DATABASE")
    ?? Path.Combine(AppContext.BaseDirectory, "kouki.sqlite");

double threshold = double.TryParse(
    thresholdArgument ?? Environment.GetEnvironmentVariable("KOUKI_THRESHOLD"),
    out double parsedThreshold)
    ? parsedThreshold
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

// Хранилище — из одной базы: задачи, журналы и состояние контекста.
// Регистрируем до AddShimiContext, чтобы TryAddSingleton не подставил InMemoryStateStore.
services.AddKagedamaData(databasePath);

services.AddMugura(
    typeof(Kouki.Ubusuna.Commands.PingHandler).Assembly,
    typeof(Kouki.Kagedama.Commands.ListTasksHandler).Assembly);
services.AddShimiContext();
services.AddUbusuna();
services.AddUn();
services.AddYasabi(o =>
{
    o.ConfidenceThreshold = threshold;
    o.Source = "mcp";
});


services.AddSingleton(new IntakeOptions
{
    ConfidenceThreshold = threshold,
    DefaultSource = "mcp",
});
services.AddSingleton<TaskIntakeService>();
services.AddSingleton<KoukiMcpTools>();
services.AddSingleton<McpSession>();

using ServiceProvider provider = services.BuildServiceProvider();

string initialized = DatabaseInitializer.Initialize(databasePath);
ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Kouki.Mcp");
logger.LogInformation("Kouki MCP: база данных {Path}", initialized);

provider.GetRequiredService<IContextRegistry>().SetDefaultContext(new BaseExecutionContext(
    "mcp",
    provider.GetRequiredService<IFileSystem>(),
    provider.GetRequiredService<ITerminal>(),
    provider.GetRequiredService<IStateStore>(),
    provider));

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
