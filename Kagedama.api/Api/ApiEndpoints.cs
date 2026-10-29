using System.Text.Json.Nodes;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Data;
using Kouki.Kagedama.Transport;
using MuguraRouter = Kouki.Mugura.Dispatcher.IRouter;
using Kouki.Shimi.Context;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Yasabi;
using Microsoft.Extensions.Options;

namespace Kouki.Kagedama.Api.Api;

/// <summary>
/// HTTP-эндпоинты Kouki: приём фраз, задачи, журнал распознавания.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>
    /// Регистрирует маршруты <c>/api/*</c>.
    /// </summary>
    /// <param name="endpoints">Построитель эндпоинтов.</param>
    /// <returns>Тот же построитель.</returns>
    public static IEndpointRouteBuilder MapKoukiApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            service = "Kagedama.api",
            utc = DateTimeOffset.UtcNow,
        }));

        // Всё, что ниже, ограничено по частоте: health оставлен без лимита, чтобы мониторинг
        // не упирался в 429, когда кто-то другой выедает квоту.
        RouteGroupBuilder api = endpoints.MapGroup(string.Empty).RequireRateLimiting("api");

        api.MapPost("/api/intake", intakeHandler);
        api.MapPost("/api/voice", voiceHandler);
        api.MapPost("/api/classify", classifyHandler);
        api.MapPost("/api/transcribe", transcribeHandler);
    api.MapPost("/api/speech", speechHandler).RequireRateLimiting("api");
        api.MapGet("/api/classifiers", (IClassifierCatalog catalog) => Results.Ok(new { classifiers = catalog.Names }));
        api.MapGet("/api/tasks", listTasksHandler);
        api.MapGet("/api/tasks/{id:guid}", getTaskHandler);
        api.MapPatch("/api/tasks/{id:guid}", patchTaskHandler);
        api.MapGet("/api/actions", listActionsHandler);
        api.MapGet("/api/context", contextHandler);
        api.MapGet("/api/classifications", listClassificationsHandler);
        api.MapPost("/api/classifications/{id:guid}/label", labelClassificationHandler);
        api.MapGet("/api/dataset", exportDatasetHandler);

        return endpoints;
    }

    /// <summary>
    /// Выгружает размеченные фразы в датасет (JSON Lines) — заготовка для обучения
    /// или для few-shot примеров модели.
    /// </summary>
    /// <param name="limit">Сколько записей брать.</param>
    /// <param name="router">Маршрутизатор команд: журнал читается командой, как и всё остальное.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private static async Task<IResult> exportDatasetHandler(
        int? limit,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        int take = Math.Clamp(limit ?? ExportDatasetHandler.DefaultLimit, 1, DatasetExport.MaxEntries);
        JsonNode? result = await router
            .RouteAsync("export_dataset", new JsonObject { ["limit"] = take }, context: null, cancellationToken)
            .ConfigureAwait(false);

        string jsonl = result?["jsonl"]?.GetValue<string>() ?? string.Empty;
        return Results.Text(jsonl, "application/x-jsonlines");
    }
    /// <summary>
    /// Что за окружение исполняет команды: имя контекста и объявленные сервисы.
    /// Нужно и человеку, и агенту — чтобы понимать, чего в этом окружении нет.
    /// </summary>
    private static IResult contextHandler(IContextRegistry registry)
    {
        IExecutionContext? context = registry.GetDefaultContext();

        if (context is null)
        {
            return Results.Ok(new { name = (string?)null, services = Array.Empty<string>() });
        }

        return Results.Ok(new
        {
            name = context.Name,
            services = context.DescribeServices(),
            fileSystem = context.FileSystem.GetType().Name,
            terminal = context.Terminal.GetType().Name,
            stateStore = context.StateStore.GetType().Name,
            hasServiceProvider = context.Services is not null,
        });
    }

    private static async Task<IResult> intakeHandler(
        IntakeRequest request,
        TaskIntakeService intake,
        IOptions<KoukiServerOptions> options,
        CancellationToken cancellationToken)
    {
        // Правила границы («источник по умолчанию», закрытый список интентов, лимит длины)
        // живут в одном месте — IntakeRequestPolicy, а не в эндпоинте и не в MCP по копии.
        if (!IntakeRequestPolicy.TryPrepare(
                request?.Text,
                request?.Source,
                request?.Intent,
                intake.DefaultSource,
                options.Value.MaxTextLength,
                out IntakeSpec? spec,
                out IntakeRejection? rejection))
        {
            return Results.BadRequest(new { code = rejection!.Code, error = rejection.Message });
        }

        IntakeResult result = await intake.IntakeAsync(
            spec!.Text,
            spec.Source,
            cancellationToken,
            intentHint: spec.IntentHint);

        return Results.Ok(ToResponse(result, intake.ConfidenceThreshold));
    }

    /// <summary>
    /// Принимает голосовую запись: WAV целиком, распознаёт, отдаёт в тот же приём.
    /// </summary>
    /// <remarks>
    /// Тело — сырой WAV (не multipart): станции и дашборду так проще, а серверу не нужно
    /// разбирать формы. Формат фиксирован: PCM, 16 бит, моно, 16 кГц.
    /// </remarks>
    private static async Task<IResult> voiceHandler(
        HttpRequest request,
        VoiceIntakeService voice,
        TaskIntakeService intake,
        ISpeechTranscriber transcriber,
        IOptions<KoukiServerOptions> options,
        CancellationToken cancellationToken)
    {
        KoukiServerOptions settings = options.Value;

        if (transcriber is Kouki.Un.UnavailableSpeechTranscriber)
        {
            return Results.Json(
                new { error = Kouki.Un.UnavailableSpeechTranscriber.Reason },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (request.ContentLength is long declared && declared > settings.MaxVoiceBytes)
        {
            return Results.Json(
                new { error = $"Запись больше {settings.MaxVoiceBytes} байт." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        (IResult? wavFailure, byte[] bytes, WavAudio? audio) = await ReadWavAsync(request, settings, cancellationToken);

        if (wavFailure is not null)
        {
            return wavFailure;
        }

        if (!IntakeRequestPolicy.TryResolveSource(request.Query["source"], TaskSources.Station, out string source, out IntakeRejection? sourceRejection))
        {
            return Results.BadRequest(new { code = sourceRejection!.Code, error = sourceRejection.Message });
        }

        // Станция присылает интент сессии: нажатие кнопки — это «запись», а не свободный текст.
        if (!IntakeRequestPolicy.TryResolveIntentHint(request.Query["intent"], out string? intentHint, out IntakeRejection? intentRejection))
        {
            return Results.BadRequest(new { code = intentRejection!.Code, error = intentRejection.Message });
        }

        VoiceIntakeResult result = await voice.AcceptAsync(
            bytes,
            source,
            new AudioFormat(audio.SampleRate, audio.Channels, AudioEncoding.Wav),
            cancellationToken,
            intentHint);

        if (result.Intake is null)
        {
            return Results.Json(
                new
                {
                    error = result.Error,
                    transcript = result.Transcription?.Text,
                    confidence = result.Transcription?.Confidence,
                    durationSec = audio.Duration.TotalSeconds,
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var response = new VoiceResponse(
            result.Transcription?.Text,
            result.Transcription?.Confidence,
            audio.Duration.TotalSeconds,
            result.AudioPath,
            ToResponse(result.Intake, intake.ConfidenceThreshold));

        return Results.Ok(response);
    }

    private static IntakeResponse ToResponse(IntakeResult result, double confidenceThreshold)
        => ApiMapping.ToResponse(result, confidenceThreshold);

    /// <summary>
    /// Одна фраза — несколько классификаторов. Ничего не исполняет и не создаёт задачу:
    /// это сравнение «роутеров» для отладки и для выбора модели.
    /// </summary>
    private static async Task<IResult> classifyHandler(
        ClassifyRequest body,
        IClassifierCatalog catalog,
        IOptions<KoukiServerOptions> options,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Text))
        {
            return Results.BadRequest(new { error = "Пустой текст: нечего классифицировать." });
        }

        KoukiServerOptions settings = options.Value;

        if (body.Text.Length > settings.MaxTextLength)
        {
            return Results.BadRequest(new { error = $"Текст длиннее {settings.MaxTextLength} символов." });
        }

        IReadOnlyList<ClassifierVerdict> verdicts = await catalog.ClassifyAsync(
            body.Text.Trim(),
            body.Classifiers,
            settings.ConfidenceThreshold,
            cancellationToken);

        return Results.Ok(new
        {
            text = body.Text.Trim(),
            threshold = settings.ConfidenceThreshold,
            classifiers = catalog.Names,
            results = verdicts,
        });
    }

    /// <summary>
    /// Только распознавание: WAV на входе, текст на выходе. Без маршрутизации и задач —
    /// чтобы проверять качество ASR отдельно от понимания фразы.
    /// </summary>
    /// <summary>
    /// Озвучивает текст: станция и бот забирают WAV 16 кГц моно и играют его.
    /// </summary>
    /// <param name="request">Текст для синтеза.</param>
    /// <param name="synthesizer">Голос (заглушка, если сборка без sherpa).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private static async Task<IResult> speechHandler(
        SpeechRequest? request,
        ISpeechSynthesizer synthesizer,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new { error = "Поле 'text' обязательно." });
        }

        if (!synthesizer.IsConfigured)
        {
            return Results.Json(
                new { error = synthesizer.UnavailableReason ?? "Озвучивание не подключено." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            SpeechSynthesisResult result = await synthesizer
                .SynthesizeAsync(request.Text, cancellationToken)
                .ConfigureAwait(false);

            return Results.File(result.Wav, "audio/wav", "speech.wav");
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(
                new { error = $"Не удалось озвучить: {ex.Message}" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
    private static async Task<IResult> transcribeHandler(
        HttpRequest request,
        ISpeechTranscriber transcriber,
        IOptions<KoukiServerOptions> options,
        CancellationToken cancellationToken)
    {
        KoukiServerOptions settings = options.Value;

        if (transcriber is Kouki.Un.UnavailableSpeechTranscriber)
        {
            return Results.Json(
                new { error = Kouki.Un.UnavailableSpeechTranscriber.Reason },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        (IResult? error, byte[] bytes, WavAudio? audio) = await ReadWavAsync(request, settings, cancellationToken);

        if (error is not null)
        {
            return error;
        }

        TranscriptionResult transcription = await transcriber.TranscribeAsync(
            new MemoryStream(bytes, writable: false),
            new AudioFormat(audio!.SampleRate, audio.Channels, AudioEncoding.Wav),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(transcription.Text))
        {
            return Results.Json(
                new { error = "Речь не распознана.", durationSec = audio.Duration.TotalSeconds },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Ok(new
        {
            transcript = transcription.Text,
            confidence = transcription.Confidence,
            durationSec = audio.Duration.TotalSeconds,
        });
    }

    /// <summary>
    /// Читает WAV из тела запроса с теми же ограничениями, что и голосовой приём.
    /// Ошибка возвращается готовым результатом, чтобы вызывающий не дублировал проверки.
    /// </summary>
    private static async Task<(IResult? Error, byte[] Bytes, WavAudio? Audio)> ReadWavAsync(
        HttpRequest request,
        KoukiServerOptions settings,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is long declared && declared > settings.MaxVoiceBytes)
        {
            return (Results.Json(
                new { error = $"Запись больше {settings.MaxVoiceBytes} байт." },
                statusCode: StatusCodes.Status413PayloadTooLarge), [], null);
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > settings.MaxVoiceBytes)
            {
                return (Results.Json(
                    new { error = $"Запись больше {settings.MaxVoiceBytes} байт." },
                    statusCode: StatusCodes.Status413PayloadTooLarge), [], null);
            }

            buffer.Write(chunk, 0, read);
        }

        byte[] bytes = buffer.ToArray();

        if (bytes.Length == 0)
        {
            return (Results.BadRequest(new { error = "Пустое тело запроса: ожидался WAV." }), [], null);
        }

        if (!WavReader.TryParse(bytes, out WavAudio? audio, out string? wavError) || audio is null)
        {
            return (Results.Json(
                new { error = wavError },
                statusCode: StatusCodes.Status415UnsupportedMediaType), bytes, null);
        }

        if (audio.Duration > TimeSpan.FromSeconds(settings.MaxVoiceSeconds))
        {
            return (Results.Json(
                new { error = $"Запись длиннее {settings.MaxVoiceSeconds} с." },
                statusCode: StatusCodes.Status413PayloadTooLarge), bytes, audio);
        }

        return (null, bytes, audio);
    }

    private static async Task<IResult> listTasksHandler(
        string? status,
        int? limit,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        if (status is not null && !TaskStatuses.IsKnown(status))
        {
            return Results.BadRequest(new { error = $"Неизвестный статус '{status}'." });
        }

        int take = Math.Clamp(limit ?? 50, 1, 500);

        // Чтение идёт через команду, как и запись: одно поведение для api, mcp и бота.
        JsonNode? result = await router.RouteAsync(
            "list_tasks",
            new JsonObject { ["status"] = status, ["limit"] = take },
            context: null,
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload && payload["error"] is JsonNode error)
        {
            return Results.BadRequest(new { error = error.ToString() });
        }

        return Results.Ok(ApiMapping.ToTaskList(result));
    }

    private static async Task<IResult> getTaskHandler(
        Guid id,
        MuguraRouter router,
        IContextRegistry contexts,
        CancellationToken cancellationToken)
    {
        // Чтение одной задачи тоже командой: хост — транспорт, а не второй способ
        // добраться до хранилища (C-11).
        JsonNode result = await router.RouteAsync(
            "get_task",
            new JsonObject { ["taskId"] = id.ToString() },
            contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);

        // Команда отвечает объектом с флагом found: «нет задачи» — это штатный ответ
        // команды, а не исключение, поэтому 404 решает хост, а не команда.
        if (result is not JsonObject payload)
        {
            return Results.NotFound(new { error = "Задача не найдена." });
        }

        if (payload["found"]?.GetValue<bool>() != true)
        {
            string? reason = payload["error"]?.GetValue<string>();
            return Results.NotFound(new { error = reason ?? "Задача не найдена." });
        }

        TaskDto? task = ApiMapping.FromTaskJson(payload);
        return task is null ? Results.NotFound(new { error = "Задача не найдена." }) : Results.Ok(task);
    }

    private static async Task<IResult> patchTaskHandler(
        Guid id,
        TaskPatchRequest request,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Тело запроса обязательно." });
        }

        JsonNode? result = await router.RouteAsync(
            "patch_task",
            new JsonObject
            {
                ["taskId"] = id.ToString(),
                ["status"] = request.Status,
                ["title"] = request.Title,
                ["priority"] = request.Priority,
            },
            context: null,
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload && payload["error"] is JsonNode error)
        {
            string text = error.ToString();
            return text.Contains("не найдена", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error = text })
                : Results.BadRequest(new { error = text });
        }

        TaskDto? updated = ApiMapping.FromTaskJson(result);
        return updated is null
            ? Results.NotFound(new { error = "Задача не найдена." })
            : Results.Ok(updated);
    }

    private static async Task<IResult> listActionsHandler(
        Guid? taskId,
        int? limit,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        // Журнал действий читается командой: у HTTP и MCP одна выдача и одна проверка.
        int take = Math.Clamp(limit ?? ListActionsHandler.DefaultLimit, 1, ListActionsHandler.MaxLimit);

        JsonNode? result = await router.RouteAsync(
            "list_actions",
            new JsonObject { ["taskId"] = taskId?.ToString(), ["limit"] = take },
            context: null,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(ApiMapping.ToActionList(result));
    }

    private static async Task<IResult> listClassificationsHandler(
        int? limit,
        bool? unresolved,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        int take = Math.Clamp(
            limit ?? ListClassificationsHandler.DefaultLimit,
            1,
            ListClassificationsHandler.MaxLimit);

        JsonNode? result = await router.RouteAsync(
            "list_classifications",
            new JsonObject { ["limit"] = take, ["unresolved"] = unresolved ?? false },
            context: null,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(ApiMapping.ToClassificationList(result));
    }

    private static async Task<IResult> labelClassificationHandler(
        Guid id,
        LabelRequest request,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Тело запроса обязательно." });
        }

        JsonNode? result = await router.RouteAsync(
            "label_classification",
            new JsonObject { ["id"] = id.ToString(), ["label"] = request.Label },
            context: null,
            cancellationToken).ConfigureAwait(false);

        if (result is not JsonObject payload)
        {
            return Results.BadRequest(new { error = "Разметка не выполнена." });
        }

        if (payload["labeled"]?.GetValue<bool>() == true)
        {
            return Results.Ok(new { id, label = payload["label"]?.GetValue<string>() });
        }

        string error = payload["error"]?.GetValue<string>() ?? "Запись не найдена.";
        string reason = payload["reason"]?.GetValue<string>() ?? "not-found";

        return reason == "invalid"
            ? Results.BadRequest(new { error })
            : Results.NotFound(new { error });
    }
}
