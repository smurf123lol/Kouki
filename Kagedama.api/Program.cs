using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Kouki.Kagedama.Api;
using Kouki.Kagedama.Api.Api;
using Kouki.Kagedama.Api.Auth;
using Kouki.Kagedama.Api.Services;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Un;
using Kouki.Ubusuna;
using Kouki.Ubusuna.DependencyInjection;
using Kouki.Yasabi;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Консоль — единственный канал логов; UTF-8, чтобы русский текст не превращался в мусор.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // Перенаправленный вывод может не поддерживать смену кодировки.
}

// Явно только консоль: EventLog в этом окружении недоступен и роняет старт.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.Configure<KoukiServerOptions>(builder.Configuration.GetSection(KoukiServerOptions.SectionName));

Assembly serverAssembly = Assembly.GetExecutingAssembly();

// Хранилище — всё из одной базы (задачи, журналы, состояние контекста).
// Регистрируем до AddShimiContext, чтобы TryAddSingleton не подставил InMemoryStateStore.
string databasePath = builder.Configuration[$"{KoukiServerOptions.SectionName}:{nameof(KoukiServerOptions.DatabasePath)}"]
    ?? "kouki.sqlite";
builder.Services.AddKagedamaData(databasePath);

builder.Services.AddMugura(
    serverAssembly,
    typeof(Kouki.Ubusuna.Commands.PingHandler).Assembly,
    typeof(Kouki.Kagedama.Commands.ListTasksHandler).Assembly);
builder.Services.AddShimiContext();
builder.Services.AddUbusuna();

// Голос: без пакета sherpa-onnx работает заглушка (503 на /api/voice),
// с -p:KoukiEnableSherpa=true подключается настоящее распознавание.
#if KOUKI_SHERPA
Kouki.Un.Sherpa.ServiceCollectionExtensions.AddUnSherpa(
    builder.Services,
    sherpa =>
    {
        sherpa.ModelDirectory = builder.Configuration[
            $"{KoukiServerOptions.SectionName}:{nameof(KoukiServerOptions.SpeechModelDirectory)}"] ?? "models";
        sherpa.Language = builder.Configuration[
            $"{KoukiServerOptions.SectionName}:{nameof(KoukiServerOptions.SpeechLanguage)}"] ?? "ru";
    });
#else
builder.Services.AddUn();
#endif

builder.Services.AddYasabi(o => o.ConfidenceThreshold = 0.75d);

// Прикладной слой не знает про IOptions: передаём ему снимок настроек.
builder.Services.AddSingleton(sp =>
{
    KoukiServerOptions serverOptions = sp.GetRequiredService<IOptions<KoukiServerOptions>>().Value;
    return new IntakeOptions
    {
        ConfidenceThreshold = serverOptions.ConfidenceThreshold,
        DefaultSource = "api",
    };
});

builder.Services.AddSingleton<TaskIntakeService>();

// Голос: распознавание приходит из Kouki.Un (пока заглушка), склейка — VoiceIntakeService.
builder.Services.AddSingleton<IAudioStore>(sp =>
{
    KoukiServerOptions serverOptions = sp.GetRequiredService<IOptions<KoukiServerOptions>>().Value;
    return new FileAudioStore(
        serverOptions.AudioDirectory,
        sp.GetRequiredService<IClassificationJournal>(),
        sp.GetRequiredService<ILogger<FileAudioStore>>());
});

builder.Services.AddSingleton(sp =>
{
    KoukiServerOptions serverOptions = sp.GetRequiredService<IOptions<KoukiServerOptions>>().Value;
    return new VoiceIntakeService(
        sp.GetRequiredService<ISpeechTranscriber>(),
        sp.GetRequiredService<TaskIntakeService>(),
        sp.GetRequiredService<ILogger<VoiceIntakeService>>(),
        serverOptions.StoreAudio ? sp.GetRequiredService<IAudioStore>() : null);
});

// Срок хранения аудио — настройка (Kouki:AudioRetentionDays, по умолчанию 30 дней).
builder.Services.AddHostedService<AudioRetentionService>();

WebApplication app = builder.Build();

KoukiServerOptions options = app.Services.GetRequiredService<IOptions<KoukiServerOptions>>().Value;
string initializedDatabase = DatabaseInitializer.Initialize(options.DatabasePath);
app.Logger.LogInformation("Kouki: база данных {Path}", initializedDatabase);

app.Services.GetRequiredService<IContextRegistry>().SetDefaultContext(new BaseExecutionContext(
    "server",
    app.Services.GetRequiredService<IFileSystem>(),
    app.Services.GetRequiredService<ITerminal>(),
    app.Services.GetRequiredService<IStateStore>(),
    app.Services));

if (string.IsNullOrWhiteSpace(options.Token))
{
    app.Logger.LogWarning("Kouki: bearer-токен не задан (Kouki:Token) — API доступен без авторизации.");
}

// Дашборд — статика из wwwroot; API остаётся под токеном (см. TokenAuthMiddleware).
string webRoot = new[]
{
    Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
}.FirstOrDefault(Directory.Exists) ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");

var files = new PhysicalFileProvider(webRoot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

// Браузер просит /favicon.ico; отдаём SVG-иконку, чтобы не было 404 в логах.
app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.ContentType = "image/svg+xml";
        await context.Response.SendFileAsync(Path.Combine(webRoot, "favicon.svg"));
        return;
    }

    await next();
});

app.UseMiddleware<TokenAuthMiddleware>();
app.MapKoukiApi();

app.Run();
