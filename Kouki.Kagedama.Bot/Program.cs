using Kouki.Kagedama.Bot;

BotOptions options = BotOptions.Load(args);

if (string.IsNullOrWhiteSpace(options.BotToken))
{
    await Console.Error.WriteLineAsync(
        "Токен бота не задан. Укажите KoukiBot:BotToken в appsettings.json, " +
        "переменную KOUKI_BOT_TOKEN или аргумент --bot-token=…");
    return 1;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

using var telegramHttp = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(options.PollTimeoutSeconds + 30),
};

using var serverHttp = new HttpClient
{
    BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(30),
};

var telegram = new TelegramApiClient(telegramHttp, options.BotToken, options.TelegramApiUrl);
var kouki = new KoukiIntakeClient(serverHttp, options.ServerToken, options.Source);
var runner = new BotRunner(telegram, kouki, options);

Console.WriteLine($"Бот Kouki запущен. Сервер: {options.ServerUrl}. Остановка — Ctrl+C.");

try
{
    await runner.RunAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
    // Штатная остановка по Ctrl+C.
}

Console.WriteLine("Бот остановлен.");
return 0;
