using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.FileProviders;
using Kouki.Kagedama.Api;
using Kouki.Kagedama.Api.Api;
using Kouki.Kagedama.Api.Auth;
using Kouki.Kagedama.Api.Services;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Hosting;
using Kouki.Shimi.Context;
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

// Необработанное исключение превращается в JSON-ответ с ProblemDetails и записью в лог,
// иначе клиент видит пустой 500 без объяснения.
builder.Services.AddProblemDetails();

// Каждый запрос — строка в логе (метод, путь, код, длительность). Без этого
// «станция не отвечает» разбирается вслепую: запросы приходят, а в логе тишина.
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestMethod
        | HttpLoggingFields.RequestPath
        | HttpLoggingFields.ResponseStatusCode
        | HttpLoggingFields.Duration;
});

// Порог живёт в одном месте: отсюда его получают и приём, и классификатор, и /api/classify.
double threshold = builder.Configuration.GetValue<double?>(
    $"{KoukiServerOptions.SectionName}:{nameof(KoukiServerOptions.ConfidenceThreshold)}")
    ?? IntakeOptions.DefaultConfidenceThreshold;

string configuredDatabase = builder.Configuration[
    $"{KoukiServerOptions.SectionName}:{nameof(KoukiServerOptions.DatabasePath)}"] ?? string.Empty;

string databasePath = string.IsNullOrWhiteSpace(configuredDatabase)
    ? KoukiHosting.DefaultDatabasePath()
    : Path.GetFullPath(configuredDatabase);

// Одна композиция домена на все хосты: хранилище, команды, контексты, классификатор, приём.
builder.Services.AddKoukiCore(new KoukiHostSettings
{
    DatabasePath = databasePath,
    ConfidenceThreshold = threshold,
    DefaultSource = TaskSources.Api,
});
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
string initializedDatabase = KoukiHosting.InitializeKouki(app.Services, "server");
app.Logger.LogInformation("Kouki: база данных {Path}", initializedDatabase);

// Защита выключена только явно: «забыли токен» — это не разрешение пускать всех.
if (string.IsNullOrWhiteSpace(options.Token) && !options.AllowAnonymous)
{
    throw new InvalidOperationException(
        "Kouki:Token не задан. Укажите bearer-токен в Kouki:Token или явно разрешите открытый API " +
        "через Kouki:AllowAnonymous=true (только для локальной отладки).");
}

app.UseExceptionHandler();

// Лог запросов идёт первым: он должен видеть и 401, и 503, и статику.
app.UseHttpLogging();

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
