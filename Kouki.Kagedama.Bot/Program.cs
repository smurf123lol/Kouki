using System.Net;
using System.Text;
using Kouki.Kagedama.Bot;

// Логи и сообщения бота — по-русски: в консоли Windows и в перенаправленном
// выводе иначе получается мусор из OEM-кодировки.
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Нет консоли (служба, перенаправление без потока) — не критично.
}

BotOptions options = BotOptions.Load(args);

if (string.IsNullOrWhiteSpace(options.BotToken))
{
    await Console.Error.WriteLineAsync(
        "Токен бота не задан. Укажите KoukiBot:BotToken в appsettings.json, " +
        "переменную KOUKI_BOT_TOKEN или аргумент --bot-token=…");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

// Один обработчик на оба клиента: общий пул соединений, но разные таймауты
// (опрос Telegram живёт дольше запроса к серверу). PooledConnectionLifetime
// заставляет переподключаться, поэтому смена адреса в DNS не требует перезапуска.
using var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(10),
    AutomaticDecompression = DecompressionMethods.All,
};

using var telegramHttp = new HttpClient(handler, disposeHandler: false)
{
    Timeout = TimeSpan.FromSeconds(options.PollTimeoutSeconds + 30),
};

using var serverHttp = new HttpClient(handler, disposeHandler: true)
{
    BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(30),
};

var telegram = new TelegramApiClient(telegramHttp, options.BotToken, options.TelegramApiUrl);
var kouki = new KoukiIntakeClient(serverHttp, options.ServerToken, options.Source);
var runner = new BotRunner(telegram, kouki, options);

Console.WriteLine($"Бот Kouki запущен. Сервер: {options.ServerUrl}. Остановка — Ctrl+C.");

int exitCode;
try
{
    exitCode = await runner.RunAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
    // Штатная остановка по Ctrl+C.
    exitCode = BotRunner.ExitOk;
}

Console.WriteLine(exitCode == BotRunner.ExitOk ? "Бот остановлен." : "Бот остановлен из-за ошибки (см. выше).");
return exitCode;
