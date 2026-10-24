using System.Text.Json.Nodes;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
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

        endpoints.MapPost("/api/intake", intakeHandler);
        endpoints.MapPost("/api/voice", voiceHandler);
        endpoints.MapPost("/api/classify", classifyHandler);
        endpoints.MapPost("/api/transcribe", transcribeHandler);
        endpoints.MapGet("/api/classifiers", (IClassifierCatalog catalog) => Results.Ok(new { classifiers = catalog.Names }));
        endpoints.MapGet("/api/tasks", listTasksHandler);
        endpoints.MapGet("/api/tasks/{id:guid}", getTaskHandler);
        endpoints.MapPatch("/api/tasks/{id:guid}", patchTaskHandler);
        endpoints.MapGet("/api/actions", listActionsHandler);
        endpoints.MapGet("/api/context", contextHandler);
        endpoints.MapGet("/api/classifications", listClassificationsHandler);
        endpoints.MapPost("/api/classifications/{id:guid}/label", labelClassificationHandler);

        return endpoints;
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
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new { error = "Поле 'text' обязательно." });
        }

        string source = string.IsNullOrWhiteSpace(request.Source) ? intake.DefaultSource : request.Source.Trim();

        if (!TaskSources.IsKnown(source))
        {
            return Results.BadRequest(new
            {
                error = $"Неизвестный источник '{source}'. Допустимые: {string.Join(", ", TaskSources.All)}.",
            });
        }

        // Сессия может сообщить интент сама («ассистент, запись» на станции): тогда фраза
        // не разбирается, а сразу становится задачей — удалять слова из записи не нужно.
        IntakeResult result = await intake.IntakeAsync(
            request.Text,
            source,
            cancellationToken,
            intentHint: request.Intent);

        return Results.Ok(ToResponse(result, options.Value.ConfidenceThreshold));
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

        string source = string.IsNullOrWhiteSpace(request.Query["source"])
            ? "station"
            : request.Query["source"].ToString().Trim();

        // Станция присылает интент сессии: нажатие кнопки — это «запись», а не свободный текст.
        string? intentHint = string.IsNullOrWhiteSpace(request.Query["intent"])
            ? null
            : request.Query["intent"].ToString().Trim();

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
            ToResponse(result.Intake, settings.ConfidenceThreshold));

        return Results.Ok(response);
    }

    private static IntakeResponse ToResponse(IntakeResult result, double confidenceThreshold)
        => new(
            result.Task is null ? null : ApiMapping.ToDto(result.Task),
            ApiMapping.ToDto(result.Route.Intent, confidenceThreshold),
            result.Route.CommandName,
            result.AutoExecuted,
            result.Outcome,
            result.Route.Error,
            result.Route.Result);

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

    private static async Task<IResult> getTaskHandler(Guid id, ITaskStore store, CancellationToken cancellationToken)
    {
        // Чтение одной задачи: команды для этого нет, поэтому читаем хранилище напрямую.
        TaskRecord? task = await store.GetAsync(id, cancellationToken);
        return task is null ? Results.NotFound(new { error = "Задача не найдена." }) : Results.Ok(ApiMapping.ToDto(task));
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
        IActionLog actions,
        CancellationToken cancellationToken)
    {
        int take = Math.Clamp(limit ?? 100, 1, 500);
        IReadOnlyList<ActionRecord> records = await actions.ListAsync(take, taskId, cancellationToken);

        return Results.Ok(records.Select(ApiMapping.ToDto).ToList());
    }

    private static async Task<IResult> listClassificationsHandler(
        int? limit,
        bool? unresolved,
        IClassificationJournal journal,
        CancellationToken cancellationToken)
    {
        int take = Math.Clamp(limit ?? 100, 1, 1000);
        IReadOnlyList<ClassificationEntry> entries =
            await journal.ListAsync(take, unresolved ?? false, cancellationToken);

        return Results.Ok(entries.Select(ApiMapping.ToDto).ToList());
    }

    private static async Task<IResult> labelClassificationHandler(
        Guid id,
        LabelRequest request,
        IClassificationJournal journal,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Label))
        {
            return Results.BadRequest(new { error = "Поле 'label' обязательно." });
        }

        bool labeled = await journal.LabelAsync(id, request.Label.Trim(), cancellationToken);
        return labeled ? Results.Ok(new { id, label = request.Label.Trim() }) : Results.NotFound(new { error = "Запись не найдена." });
    }
}
