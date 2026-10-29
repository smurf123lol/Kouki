using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using System.Threading.RateLimiting;
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

// Пути приводятся к абсолютным до регистрации: иначе база, аудио и модель окажутся там,
// где их никто не искал (относительный путь раньше разрешался от текущего каталога процесса).
KoukiServerOptions serverOptions = new();
builder.Configuration.GetSection(KoukiServerOptions.SectionName).Bind(serverOptions);

string configuredDatabase = serverOptions.DatabasePath;
string configuredAudio = serverOptions.AudioDirectory;
string configuredModel = serverOptions.SpeechModelDirectory;

serverOptions.DatabasePath = KoukiPaths.Resolve(
    serverOptions.DatabasePath, "kouki.sqlite", nameof(KoukiServerOptions.DatabasePath), serverOptions.RequireAbsolutePaths);
serverOptions.AudioDirectory = KoukiPaths.Resolve(
    serverOptions.AudioDirectory, "audio", nameof(KoukiServerOptions.AudioDirectory), serverOptions.RequireAbsolutePaths);
serverOptions.SpeechModelDirectory = KoukiPaths.Resolve(
    serverOptions.SpeechModelDirectory, "models", nameof(KoukiServerOptions.SpeechModelDirectory), serverOptions.RequireAbsolutePaths);

// Один экземпляр настроек на всё приложение: и IOptions, и прямой резолв дают уже разрешённые пути.
builder.Services.AddSingleton(serverOptions);
builder.Services.AddSingleton<IOptions<KoukiServerOptions>>(Options.Create(serverOptions));

// Необработанное исключение превращается в JSON-ответ с ProblemDetails и записью в лог,
// иначе клиент видит пустой 500 без объяснения.
builder.Services.AddProblemDetails();

// Ограничение частоты: сервер живёт в локальной сети, но кнопка на станции и автообновление
// дашборда — это регулярный поток. Лимит на ключ (токен, иначе адрес) защищает от цикла.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
    {
        string key = httpContext.Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(key))
        {
            key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

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
double threshold = serverOptions.ConfidenceThreshold;
string databasePath = serverOptions.DatabasePath;

// Ответчик на вопросы (команда ask): провайдер, ключ и модель — из Kouki:Answerer.
// Ключа может не быть — тогда отвечает заглушка, а причина видна в /api/context и логе.
Kouki.Kotae.KotaeOptions answererOptions = new();
builder.Configuration.GetSection($"{KoukiHostSettings.SectionName}:Answerer").Bind(answererOptions);

// Одна композиция домена на все хосты: хранилище, команды, контексты, классификатор, приём.
builder.Services.AddKoukiCore(new KoukiHostSettings
{
    DatabasePath = databasePath,
    ConfidenceThreshold = threshold,
    DefaultSource = TaskSources.Api,
    Answerer = answererOptions,
});
builder.Services.AddUbusuna();

// Голос: без пакета sherpa-onnx работает заглушка (503 на /api/voice),
// с -p:KoukiEnableSherpa=true подключается настоящее распознавание.
// Модели может не быть (свежая установка, чужой сервер): это повод работать
// без распознавания, а не падать на старте — поэтому каталог проверяем до
// регистрации распознавателя.
string? speechFallbackReason = null;
#if KOUKI_SHERPA
if (Kouki.Un.SpeechModelCheck.TryValidate(serverOptions.SpeechModelDirectory, out string? modelProblem))
{
    Kouki.Un.Sherpa.ServiceCollectionExtensions.AddUnSherpa(
        builder.Services,
        sherpa =>
        {
            sherpa.ModelDirectory = serverOptions.SpeechModelDirectory;
            sherpa.Language = serverOptions.SpeechLanguage;
        });
}
else
{
    speechFallbackReason = modelProblem;
    builder.Services.AddUn();
}
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
app.Logger.LogInformation(
    "Kouki: аудио {Audio}, модель {Model}, язык {Language}, порог {Threshold}",
    options.AudioDirectory,
    options.SpeechModelDirectory,
    options.SpeechLanguage,
    options.ConfidenceThreshold);

// Относительный путь в конфигурации — не ошибка, но на сервере это почти всегда сюрприз.
LogResolvedPath(app.Logger, nameof(KoukiServerOptions.DatabasePath), configuredDatabase, options.DatabasePath);
LogResolvedPath(app.Logger, nameof(KoukiServerOptions.AudioDirectory), configuredAudio, options.AudioDirectory);
LogResolvedPath(app.Logger, nameof(KoukiServerOptions.SpeechModelDirectory), configuredModel, options.SpeechModelDirectory);

// Ловушка, на которую уже наступали: обычная сборка (без -p:KoukiEnableSherpa=true)
// перезаписывает assets и тихо оставляет заглушку — станция получает 503 и сообщает
// «распознавание не подключено». Предупреждаем сразу, в момент старта.
if (app.Services.GetService<ISpeechTranscriber>() is Kouki.Un.UnavailableSpeechTranscriber)
{
    if (speechFallbackReason is not null)
    {
        app.Logger.LogWarning(
            "Kouki: распознавание речи выключено — {Reason}. /api/voice будет отвечать 503. " +
            "Скопируйте каталог модели (tokens.txt, *encoder*.onnx, *decoder*.onnx) " +
            "в {Model} или задайте другой Kouki:SpeechModelDirectory.",
            speechFallbackReason,
            options.SpeechModelDirectory);
    }
    else
    {
        app.Logger.LogWarning(
            "Kouki: распознавание речи не подключено — сервер собран без -p:KoukiEnableSherpa=true. " +
            "/api/voice будет отвечать 503. Пересборка: dotnet restore Kagedama.api\\Kagedama.api.csproj " +
            "-p:KoukiEnableSherpa=true && dotnet build Kagedama.api\\Kagedama.api.csproj " +
            "-p:KoukiEnableSherpa=true --no-restore (или tools/run-server.ps1).");
    }
}

// Защита выключена только явно: «забыли токен» — это не разрешение пускать всех.
if (string.IsNullOrWhiteSpace(options.Token) && !options.AllowAnonymous)
{
    throw new InvalidOperationException(
        "Kouki:Token не задан. Укажите bearer-токен в Kouki:Token или явно разрешите открытый API " +
        "через Kouki:AllowAnonymous=true (только для локальной отладки).");
}

app.UseExceptionHandler();

// Заголовки безопасности ставятся раньше остальных: они должны попасть и в статику,
// и в ответы API, включая отказы по токену.
app.Use(async (context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'; " +
        "frame-ancestors 'none'; object-src 'none'";
    await next(context);
});

// Лог запросов идёт первым: он должен видеть и 401, и 503, и статику.
app.UseHttpLogging();

// Лимит ставится после лога (чтобы 429 тоже был виден) и до статики: статику не ограничиваем,
// ограничение висит только на группе /api.
app.UseRateLimiter();

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

static void LogResolvedPath(ILogger logger, string setting, string configured, string resolved)
{
    if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathRooted(configured))
    {
        logger.LogWarning(
            "Путь {Setting} = '{Configured}' относительный: разрешён от каталога приложения как {Resolved}. " +
            "На сервере задавайте абсолютный путь (Kouki__{Setting}) и включите Kouki:RequireAbsolutePaths.",
            setting,
            configured,
            resolved,
            setting);
    }
}
