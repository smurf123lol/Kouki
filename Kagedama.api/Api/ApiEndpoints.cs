using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Data;
using Kouki.Kagedama.Transport;
using Kouki.Kagedama.Api.Services;
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
    /// <summary>Сколько секунд клиент может кэшировать запись: аудио неизменно после записи.</summary>
    private const int AudioCacheSeconds = 3600;

    /// <summary>Текст 404 аудио: нет записи журнала, нет пути или нет файла.</summary>
    private const string AudioNotFoundError = "Записи нет.";

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
        api.MapPost("/api/tasks", createTaskHandler);
        api.MapPost("/api/tasks/{id:guid}/move", moveTaskHandler);
        api.MapDelete("/api/tasks/{id:guid}", deleteTaskHandler);
        api.MapPatch("/api/tasks/{id:guid}", patchTaskHandler);
        api.MapGet("/api/actions", listActionsHandler);
        api.MapGet("/api/telemetry", telemetryHandler);
        api.MapGet("/api/context", contextHandler);
        api.MapGet("/api/classifications", listClassificationsHandler);
        api.MapGet("/api/audio/{id:guid}", classificationAudioHandler);
        api.MapPost("/api/classifications/{id:guid}/label", labelClassificationHandler);
        api.MapGet("/api/dataset", exportDatasetHandler);
        api.MapGet("/api/reminders", listRemindersHandler);
        api.MapGet("/api/reminders/due", dueRemindersHandler);
        api.MapPost("/api/reminders/{id:guid}/ack", acknowledgeReminderHandler);
        api.MapPost("/api/reminders/{id:guid}/snooze", snoozeReminderHandler);
        api.MapGet("/api/firmware", firmwareInfoHandler);
        api.MapGet("/api/firmware/image", firmwareImageHandler);
        api.MapGet("/api/questions", listQuestionsHandler);
        api.MapPost("/api/questions/{id:guid}/task", createQuestionTaskHandler);

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
        // Правила границы («источник по умолчанию», закрытый список интентов, лимит длины,
        // теги) живут в одном месте — IntakeRequestPolicy, а не в эндпоинте и не в MCP по копии.
        // Источник разрешаем заранее: от него зависит режим тегов (extract/keep).
        if (!IntakeRequestPolicy.TryResolveSource(
                request?.Source,
                intake.DefaultSource,
                out string source,
                out IntakeRejection? sourceRejection))
        {
            return Results.BadRequest(new { code = sourceRejection!.Code, error = sourceRejection.Message });
        }

        if (!IntakeRequestPolicy.TryPrepare(
                request?.Text,
                source,
                request?.Intent,
                intake.DefaultSource,
                options.Value.MaxTextLength,
                out IntakeSpec? spec,
                out IntakeRejection? rejection,
                intake.ResolveTagMode(source),
                request?.Dialogue))
        {
            return Results.BadRequest(new { code = rejection!.Code, error = rejection.Message });
        }

        IntakeResult result;
        try
        {
            result = await intake.IntakeAsync(
                spec!.Text,
                spec.Source,
                cancellationToken,
                intentHint: spec.IntentHint,
                tags: spec.Tags,
                continueDialogue: spec.ContinueDialogue);
        }
        catch (IntakeRejectionException ex)
        {
            // Защита приёма для прямых вызовов: фраза из одних тегов — отказ клиента, не 500.
            return Results.BadRequest(new { code = ex.Code, error = ex.Message });
        }

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
        IRequestLog requestLog,
        IClassificationJournal journal,
        IOptions<KoukiServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        KoukiServerOptions settings = options.Value;

        string source = IntakeRequestPolicy.TryResolveSource(request.Query["source"], TaskSources.Station, out string resolvedSrc, out _)
            ? resolvedSrc
            : TaskSources.Station;

        async Task RecordTelemetryAsync(
            int httpStatus,
            string outcome,
            string? error,
            long? audioBytes = null,
            double? audioDurationSec = null,
            string? intent = null,
            Guid? taskId = null,
            Guid? classificationId = null)
        {
            if (!settings.RequestLogEnabled) return;
            try
            {
                var entry = new RequestLogEntry(
                    Id: Guid.NewGuid(),
                    CreatedAt: DateTimeOffset.UtcNow,
                    Endpoint: RequestEndpoints.Voice,
                    Source: source,
                    HttpStatus: httpStatus,
                    Outcome: outcome,
                    DurationMs: sw.ElapsedMilliseconds,
                    Intent: intent,
                    TaskId: taskId,
                    ClassificationId: classificationId,
                    AudioBytes: audioBytes,
                    AudioDurationSec: audioDurationSec,
                    Error: error);

                await requestLog.RecordAsync(entry, settings.RequestLogMaxRows, cancellationToken);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Kouki.Kagedama.Api").LogWarning(ex, "Не удалось записать журнал запросов телеметрии.");
            }
        }

        if (transcriber is Kouki.Un.UnavailableSpeechTranscriber)
        {
            string err = Kouki.Un.UnavailableSpeechTranscriber.Reason;
            await RecordTelemetryAsync(StatusCodes.Status503ServiceUnavailable, RequestOutcomes.Error, err);
            return Results.Json(
                new { error = err },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (request.ContentLength is long declared && declared > settings.MaxVoiceBytes)
        {
            string err = $"Запись больше {settings.MaxVoiceBytes} байт.";
            await RecordTelemetryAsync(StatusCodes.Status413PayloadTooLarge, RequestOutcomes.Rejected, err);
            return Results.Json(
                new { error = err },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        ReadWavResult wav = await ReadWavAsync(request, settings, cancellationToken);

        if (wav.Error is not null)
        {
            await RecordTelemetryAsync(
                wav.StatusCode ?? StatusCodes.Status400BadRequest,
                RequestOutcomes.Rejected,
                wav.ErrorMessage,
                wav.Bytes.Length,
                wav.Audio?.Duration.TotalSeconds);
            return wav.Error;
        }

        byte[] bytes = wav.Bytes;
        WavAudio audio = wav.Audio!;

        if (!IntakeRequestPolicy.TryResolveSource(request.Query["source"], TaskSources.Station, out source, out IntakeRejection? sourceRejection))
        {
            await RecordTelemetryAsync(
                StatusCodes.Status400BadRequest,
                RequestOutcomes.Rejected,
                sourceRejection!.Message,
                bytes.Length,
                audio.Duration.TotalSeconds);
            return Results.BadRequest(new { code = sourceRejection.Code, error = sourceRejection.Message });
        }

        // Станция присылает интент сессии: нажатие кнопки — это «запись», а не свободный текст.
        if (!IntakeRequestPolicy.TryResolveIntentHint(request.Query["intent"], out string? intentHint, out IntakeRejection? intentRejection))
        {
            await RecordTelemetryAsync(
                StatusCodes.Status400BadRequest,
                RequestOutcomes.Rejected,
                intentRejection!.Message,
                bytes.Length,
                audio.Duration.TotalSeconds);
            return Results.BadRequest(new { code = intentRejection.Code, error = intentRejection.Message });
        }

        if (!IntakeRequestPolicy.TryResolveDialogueContinue(request.Query["dialogue"], out bool continueDialogue, out IntakeRejection? dialogueRejection))
        {
            await RecordTelemetryAsync(
                StatusCodes.Status400BadRequest,
                RequestOutcomes.Rejected,
                dialogueRejection!.Message,
                bytes.Length,
                audio.Duration.TotalSeconds);
            return Results.BadRequest(new { code = dialogueRejection.Code, error = dialogueRejection.Message });
        }

        VoiceIntakeResult result;
        try
        {
            result = await voice.AcceptAsync(
                bytes,
                source,
                new AudioFormat(audio.SampleRate, audio.Channels, AudioEncoding.Wav),
                cancellationToken,
                intentHint,
                continueDialogue);
        }
        catch (IntakeRejectionException ex)
        {
            // Распознанное — это только теги: приём отказал по правилам, а не сломался.
            await RecordTelemetryAsync(
                StatusCodes.Status400BadRequest,
                RequestOutcomes.Rejected,
                ex.Message,
                bytes.Length,
                audio.Duration.TotalSeconds);
            return Results.BadRequest(new { code = ex.Code, error = ex.Message });
        }

        if (result.Intake is null)
        {
            await RecordTelemetryAsync(
                StatusCodes.Status422UnprocessableEntity,
                RequestOutcomes.Rejected,
                result.Error,
                bytes.Length,
                audio.Duration.TotalSeconds);
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

        long durationMs = sw.ElapsedMilliseconds;
        long audioBytes = bytes.Length;
        double audioDurationSec = audio.Duration.TotalSeconds;
        Guid? classificationId = result.Intake.Route.Intent.JournalId;
        Guid? taskId = result.Intake.Task?.Id;
        string? intent = result.Intake.Route.Intent.Intent;

        await RecordTelemetryAsync(
            StatusCodes.Status200OK,
            RequestOutcomes.Ok,
            null,
            audioBytes,
            audioDurationSec,
            intent,
            taskId,
            classificationId);

        if (classificationId is { } classId && classId != Guid.Empty)
        {
            try
            {
                await journal.AnnotateMetricsAsync(classId, audioBytes, audioDurationSec, durationMs, cancellationToken);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Kouki.Kagedama.Api").LogWarning(ex, "Не удалось обновить метрики в classifications.");
            }
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
        IRequestLog requestLog,
        IOptions<KoukiServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        KoukiServerOptions settings = options.Value;
        string source = "api";

        async Task RecordTelemetryAsync(int httpStatus, string outcome, string? error)
        {
            if (!settings.RequestLogEnabled) return;
            try
            {
                var entry = new RequestLogEntry(
                    Id: Guid.NewGuid(),
                    CreatedAt: DateTimeOffset.UtcNow,
                    Endpoint: RequestEndpoints.Classify,
                    Source: source,
                    HttpStatus: httpStatus,
                    Outcome: outcome,
                    DurationMs: sw.ElapsedMilliseconds,
                    Error: error);

                await requestLog.RecordAsync(entry, settings.RequestLogMaxRows, cancellationToken);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Kouki.Kagedama.Api").LogWarning(ex, "Не удалось записать журнал запросов телеметрии.");
            }
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Text))
        {
            string err = "Пустой текст: нечего классифицировать.";
            await RecordTelemetryAsync(StatusCodes.Status400BadRequest, RequestOutcomes.Rejected, err);
            return Results.BadRequest(new { error = err });
        }

        if (body.Text.Length > settings.MaxTextLength)
        {
            string err = $"Текст длиннее {settings.MaxTextLength} символов.";
            await RecordTelemetryAsync(StatusCodes.Status400BadRequest, RequestOutcomes.Rejected, err);
            return Results.BadRequest(new { error = err });
        }

        IReadOnlyList<ClassifierVerdict> verdicts = await catalog.ClassifyAsync(
            body.Text.Trim(),
            body.Classifiers,
            settings.ConfidenceThreshold,
            cancellationToken);

        await RecordTelemetryAsync(StatusCodes.Status200OK, RequestOutcomes.Ok, null);

        return Results.Ok(new
        {
            text = body.Text.Trim(),
            threshold = settings.ConfidenceThreshold,
            classifiers = catalog.Names,
            results = verdicts,
        });
    }

    /// <summary>
    /// Сводка телеметрии запросов к API за выбранное временное окно.
    /// </summary>
    private static async Task<IResult> telemetryHandler(
        HttpRequest request,
        MuguraRouter router,
        IContextRegistry contexts,
        CancellationToken cancellationToken)
    {
        string? rawHours = request.Query["hours"];
        int hours = ListTelemetryHandler.DefaultHours;
        if (!string.IsNullOrWhiteSpace(rawHours))
        {
            if (!int.TryParse(rawHours, out hours) || hours < ListTelemetryHandler.MinHours || hours > ListTelemetryHandler.MaxHours)
            {
                return Results.BadRequest(new { error = "hours: целое число от 1 до 168." });
            }
        }

        JsonNode? result = await router.RouteAsync(
            "list_telemetry",
            new JsonObject { ["hours"] = hours },
            contexts.GetDefaultContext(),
            cancellationToken);

        if (result is JsonObject obj && obj["error"] is JsonNode err && err.GetValueKind() == JsonValueKind.String)
        {
            return Results.BadRequest(new { error = err.GetValue<string>() });
        }

        return Results.Ok(result);
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
    /// <param name="loggerFactory">Фабрика логгеров.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private static async Task<IResult> speechHandler(
        SpeechRequest? request,
        ISpeechSynthesizer synthesizer,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new { error = "Поле 'text' обязательно." });
        }

        if (request.Voice is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Voice))
            {
                return Results.BadRequest(new { error = "Поле 'voice' пустое: нечего выбирать." });
            }

            if (!Kouki.Un.SpeechVoiceResolver.TryValidateVoiceName(request.Voice, out string? voiceError))
            {
                return Results.BadRequest(new { error = voiceError });
            }
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
                .SynthesizeAsync(request.Text, request.Voice, cancellationToken)
                .ConfigureAwait(false);

            return Results.File(result.Wav, "audio/wav", "speech.wav");
        }
        catch (ArgumentException ex)
        {
            loggerFactory.CreateLogger("Kouki.Kagedama.Api").LogWarning("Ошибка выбора голоса для синтеза: {Error}", ex.Message);
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

        ReadWavResult wav = await ReadWavAsync(request, settings, cancellationToken);

        if (wav.Error is not null)
        {
            return wav.Error;
        }

        byte[] bytes = wav.Bytes;
        WavAudio audio = wav.Audio!;

        TranscriptionResult transcription = await transcriber.TranscribeAsync(
            new MemoryStream(bytes, writable: false),
            new AudioFormat(audio.SampleRate, audio.Channels, AudioEncoding.Wav),
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
    /// Результат чтения WAV-потока из тела HTTP-запроса.
    /// </summary>
    private sealed record ReadWavResult(IResult? Error, string? ErrorMessage, int? StatusCode, byte[] Bytes, WavAudio? Audio);

    /// <summary>
    /// Читает WAV из тела запроса с теми же ограничениями, что и голосовой приём.
    /// Ошибка возвращается готовым результатом, чтобы вызывающий не дублировал проверки.
    /// </summary>
    private static async Task<ReadWavResult> ReadWavAsync(
        HttpRequest request,
        KoukiServerOptions settings,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is long declared && declared > settings.MaxVoiceBytes)
        {
            string msg = $"Запись больше {settings.MaxVoiceBytes} байт.";
            return new ReadWavResult(Results.Json(
                new { error = msg },
                statusCode: StatusCodes.Status413PayloadTooLarge), msg, StatusCodes.Status413PayloadTooLarge, [], null);
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > settings.MaxVoiceBytes)
            {
                string msg = $"Запись больше {settings.MaxVoiceBytes} байт.";
                return new ReadWavResult(Results.Json(
                    new { error = msg },
                    statusCode: StatusCodes.Status413PayloadTooLarge), msg, StatusCodes.Status413PayloadTooLarge, [], null);
            }

            buffer.Write(chunk, 0, read);
        }

        byte[] bytes = buffer.ToArray();

        if (bytes.Length == 0)
        {
            string msg = "Пустое тело запроса: ожидался WAV.";
            return new ReadWavResult(Results.BadRequest(new { error = msg }), msg, StatusCodes.Status400BadRequest, [], null);
        }

        if (!WavReader.TryParse(bytes, out WavAudio? audio, out string? wavError) || audio is null)
        {
            string msg = wavError ?? "Ошибка формата WAV.";
            return new ReadWavResult(Results.Json(
                new { error = msg },
                statusCode: StatusCodes.Status415UnsupportedMediaType), msg, StatusCodes.Status415UnsupportedMediaType, bytes, null);
        }

        if (audio.Duration > TimeSpan.FromSeconds(settings.MaxVoiceSeconds))
        {
            string msg = $"Запись длиннее {settings.MaxVoiceSeconds} с.";
            return new ReadWavResult(Results.Json(
                new { error = msg },
                statusCode: StatusCodes.Status413PayloadTooLarge), msg, StatusCodes.Status413PayloadTooLarge, bytes, audio);
        }

        return new ReadWavResult(null, null, null, bytes, audio);
    }

    private static async Task<IResult> listTasksHandler(
        string? status,
        int? limit,
        string? tag,
        string? parentId,
        bool? topLevel,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        if (status is not null && !TaskStatuses.IsKnown(status))
        {
            return Results.BadRequest(new { error = $"Неизвестный статус '{status}'." });
        }

        // Пустой parentId — это «отфильтруй по пустому родителю», а не «покажи всё» (E4):
        // такой фильтр не имеет смысла, поэтому это ошибка клиента.
        string? parentFilter = null;
        if (parentId is not null)
        {
            if (string.IsNullOrWhiteSpace(parentId))
            {
                return Results.BadRequest(new { error = TaskCommands.MissingTaskIdError });
            }

            if (!Guid.TryParse(parentId, out Guid parsedParent))
            {
                return Results.BadRequest(new { error = $"'{parentId}' — не идентификатор задачи." });
            }

            parentFilter = parsedParent.ToString();
        }

        // Тег фильтрует задачи: «#» и скобки снимает нормализация, но пустой тег — ошибка клиента.
        // Параметр отсутствует (null) — фильтра нет; параметр есть и пуст после нормализации — 400:
        // «?tag=» значит «отфильтруй по пустому тегу», а не «покажи всё» (E4).
        string? tagFilter = null;
        if (tag is not null)
        {
            tagFilter = TagParser.Normalize(tag);
            if (tagFilter.Length == 0)
            {
                return Results.BadRequest(new { error = ListTasksHandler.EmptyTagError });
            }
        }

        int take = Math.Clamp(limit ?? 50, 1, 500);

        var slots = new JsonObject
        {
            ["status"] = status,
            ["limit"] = take,
            ["tag"] = tagFilter,
        };
        if (parentFilter is not null)
        {
            slots["parentId"] = parentFilter;
        }

        if (topLevel == true)
        {
            slots["topLevel"] = true;
        }

        // Чтение идёт через команду, как и запись: одно поведение для api, mcp и бота.
        JsonNode? result = await router.RouteAsync(
            "list_tasks",
            slots,
            context: null,
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload && payload["error"] is JsonNode error)
        {
            return Results.BadRequest(new { error = error.ToString() });
        }

        return Results.Ok(ApiMapping.ToTaskList(result));
    }

    private static async Task<IResult> createTaskHandler(
        TaskCreateRequest? request,
        MuguraRouter router,
        IContextRegistry contexts,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Тело запроса обязательно." });
        }

        // Создание — только командой: правила пустого текста, родителя и приоритета живут
        // в add_task, а не второй копией в эндпоинте.
        JsonNode? result = await router.RouteAsync(
            "add_task",
            new JsonObject
            {
                ["text"] = request.Text,
                ["source"] = TaskSources.Dashboard,
                ["parentId"] = request.ParentId,
                ["priority"] = request.Priority,
            },
            contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload && payload["error"] is JsonNode error)
        {
            return Results.BadRequest(new { error = error.ToString() });
        }

        TaskDto? created = ApiMapping.FromTaskJson(result);
        return created is null
            ? Results.BadRequest(new { error = "Пустая фраза: нечего записывать как задачу." })
            : Results.Ok(created);
    }

    private static async Task<IResult> moveTaskHandler(
        Guid id,
        TaskMoveRequest? request,
        MuguraRouter router,
        IContextRegistry contexts,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Тело запроса обязательно." });
        }

        JsonNode? result = await router.RouteAsync(
            "move_task",
            new JsonObject
            {
                ["taskId"] = id.ToString(),
                ["status"] = request.Status,
                ["position"] = request.Position,
            },
            contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload && payload["error"] is JsonNode error)
        {
            string text = error.ToString();
            return text.Contains("не найдена", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error = text })
                : Results.BadRequest(new { error = text });
        }

        TaskDto? moved = ApiMapping.FromTaskJson(result);
        return moved is null
            ? Results.NotFound(new { error = $"Задача {id} не найдена." })
            : Results.Ok(moved);
    }

    private static async Task<IResult> deleteTaskHandler(
        Guid id,
        string? mode,
        bool? confirm,
        MuguraRouter router,
        IContextRegistry contexts,
        CancellationToken cancellationToken)
    {
        string? requestedMode = string.IsNullOrWhiteSpace(mode) ? null : mode;

        if (requestedMode is not null && !TaskDeleteModes.IsKnown(requestedMode))
        {
            return Results.BadRequest(new { error = $"Неизвестный режим удаления '{mode}'." });
        }

        TaskDeleteMode deleteMode = TaskDeleteModes.Parse(requestedMode);

        // Каскад задевает чужие карточки, поэтому без явного confirm=true его не запускаем.
        // Detach трогает только саму задачу — подтверждение ему не нужно (§2.5).
        if (deleteMode == TaskDeleteMode.Cascade && confirm != true)
        {
            return Results.BadRequest(new { error = "Удаление с подзадачами требует подтверждения." });
        }

        string command = deleteMode == TaskDeleteMode.Detach
            ? "delete_task_detach"
            : "delete_task_cascade";

        JsonNode? result = await router.RouteAsync(
            command,
            new JsonObject { ["taskId"] = id.ToString() },
            contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload && payload["error"] is JsonNode error)
        {
            string text = error.ToString();
            return text.Contains("не найдена", StringComparison.OrdinalIgnoreCase)
                ? Results.NotFound(new { error = text })
                : Results.BadRequest(new { error = text });
        }

        return Results.Ok(result);
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
        string? q,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        // Журнал действий читается командой: у HTTP и MCP одна выдача и одна проверка.
        int take = Math.Clamp(limit ?? ListActionsHandler.DefaultLimit, 1, ListActionsHandler.MaxLimit);

        var args = new JsonObject
        {
            ["taskId"] = taskId?.ToString(),
            ["limit"] = take,
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            args["query"] = q.Trim();
        }

        JsonNode? result = await router.RouteAsync(
            "list_actions",
            args,
            context: null,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(ApiMapping.ToActionList(result));
    }

    private static async Task<IResult> listClassificationsHandler(
        Guid? taskId,
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
            new JsonObject
            {
                ["taskId"] = taskId?.ToString(),
                ["limit"] = take,
                ["unresolved"] = unresolved ?? false,
            },
            context: null,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(ApiMapping.ToClassificationList(result));
    }

    /// <summary>
    /// Отдаёт сохранённую запись разговора (WAV) по идентификатору записи журнала.
    /// </summary>
    /// <remarks>
    /// Чтение мимо команды (инвариант 7): WAV — двоичные байты, через JSON-команды они не
    /// проходят. Путь берётся только из журнала, а не из запроса; файл обязан лежать внутри
    /// <see cref="KoukiServerOptions.AudioDirectory"/>, иначе ответ 404.
    /// </remarks>
    /// <param name="id">Запись журнала.</param>
    /// <param name="journal">Журнал: единственный источник пути к аудио.</param>
    /// <param name="options">Настройки сервера: корень каталога аудио.</param>
    /// <param name="loggers">Фабрика логов: отказы видны в журнале сервера.</param>
    /// <param name="httpContext">Ответ: заголовок кэширования.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private static async Task<IResult> classificationAudioHandler(
        Guid id,
        IClassificationJournal journal,
        IOptions<KoukiServerOptions> options,
        ILoggerFactory loggers,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ClassificationEntry? entry = await journal.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is null || string.IsNullOrWhiteSpace(entry.AudioPath))
        {
            return Results.NotFound(new { error = AudioNotFoundError });
        }

        string root = Path.GetFullPath(options.Value.AudioDirectory);
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Путь только из базы: сначала нормализуем его (это раскрывает «..») и проверяем,
        // что результат лежит внутри каталога аудио. Относительные пути не принимаем.
        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(entry.AudioPath))
            {
                return Results.NotFound(new { error = AudioNotFoundError });
            }

            fullPath = Path.GetFullPath(entry.AudioPath);
        }
        catch (Exception)
        {
            return Results.NotFound(new { error = AudioNotFoundError });
        }

        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            // В базе мог остаться путь за пределы каталога аудио: не отдаём ни байта.
            ILogger logger = loggers.CreateLogger(typeof(ApiEndpoints).FullName!);
            logger.LogWarning(
                "Аудио записи {Id} не отдано: путь {AudioPath} вне каталога {AudioRoot}.",
                id,
                entry.AudioPath,
                root);

            return Results.NotFound(new { error = AudioNotFoundError });
        }

        // Защита от symlink-обхода: проверяем, куда реально указывает файл
        try
        {
            FileInfo fileInfo = new(fullPath);
            if (!fileInfo.Exists)
            {
                return Results.NotFound(new { error = AudioNotFoundError });
            }

            string finalTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
            string normalizedTarget = Path.GetFullPath(finalTarget);
            if (!normalizedTarget.StartsWith(rootPrefix, comparison))
            {
                ILogger logger = loggers.CreateLogger(typeof(ApiEndpoints).FullName!);
                logger.LogWarning(
                    "Аудио записи {Id} не отдано: symlink {AudioPath} указывает за пределы каталога аудио ({FinalTarget}).",
                    id,
                    entry.AudioPath,
                    normalizedTarget);

                return Results.NotFound(new { error = AudioNotFoundError });
            }
        }
        catch (Exception ex)
        {
            ILogger logger = loggers.CreateLogger(typeof(ApiEndpoints).FullName!);
            logger.LogWarning(ex, "Ошибка проверки пути аудио для записи {Id}.", id);
            return Results.NotFound(new { error = AudioNotFoundError });
        }

        // Защита от TOCTOU: файл мог быть удалён очисткой между проверкой и чтением
        byte[] wav;
        try
        {
            wav = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            ILogger logger = loggers.CreateLogger(typeof(ApiEndpoints).FullName!);
            logger.LogWarning(ex, "Аудио записи {Id} не удалось прочитать ({AudioPath}).", id, fullPath);
            return Results.NotFound(new { error = AudioNotFoundError });
        }

        httpContext.Response.Headers.CacheControl = $"private, max-age={AudioCacheSeconds}";
        return Results.File(wav, "audio/wav");
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

    /// <summary>
    /// Список напоминаний: по умолчанию все, с <c>open=true</c> — только живые.
    /// </summary>
    /// <param name="open">Только ожидающие срока или реакции.</param>
    /// <param name="limit">Сколько вернуть.</param>
    /// <param name="router">Маршрутизатор команд.</param>
    /// <param name="cancellationToken">Отмена.</param>
    private static async Task<IResult> listRemindersHandler(
        bool? open,
        int? limit,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["openOnly"] = open ?? false,
            ["limit"] = limit ?? 20,
        };

        JsonNode? result = await router
            .RouteAsync("list_reminders", payload, context: null, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new { reminders = ApiMapping.ToReminderList(result) });
    }

    /// <summary>
    /// Забирает созревшие напоминания: канал доставки получает их и идёт отправлять.
    /// </summary>
    /// <param name="limit">Сколько забрать.</param>
    /// <param name="router">Маршрутизатор команд.</param>
    /// <param name="cancellationToken">Отмена.</param>
    private static async Task<IResult> dueRemindersHandler(
        int? limit,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["limit"] = limit ?? 5 };
        JsonNode? result = await router
            .RouteAsync("take_due_reminders", payload, context: null, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new { reminders = ApiMapping.ToReminderList(result) });
    }

    /// <summary>
    /// Подтверждает напоминание.
    /// </summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="router">Маршрутизатор команд.</param>
    /// <param name="cancellationToken">Отмена.</param>
    private static async Task<IResult> acknowledgeReminderHandler(
        Guid id,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["id"] = id.ToString() };
        JsonNode? result = await router
            .RouteAsync("complete_reminder", payload, context: null, cancellationToken)
            .ConfigureAwait(false);

        bool done = result?["done"]?.GetValue<bool>() ?? false;
        return done
            ? Results.Ok(new { done = true })
            : Results.NotFound(new { error = "Напоминание не найдено." });
    }

    /// <summary>
    /// Откладывает напоминание.
    /// </summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="minutes">На сколько минут отложить.</param>
    /// <param name="router">Маршрутизатор команд.</param>
    /// <param name="cancellationToken">Отмена.</param>
    private static async Task<IResult> snoozeReminderHandler(
        Guid id,
        int? minutes,
        MuguraRouter router,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["id"] = id.ToString(),
            ["minutes"] = minutes ?? 10,
        };

        JsonNode? result = await router
            .RouteAsync("snooze_reminder", payload, context: null, cancellationToken)
            .ConfigureAwait(false);

        bool snoozed = result?["snoozed"]?.GetValue<bool>() ?? false;
        if (!snoozed)
        {
            return Results.NotFound(new { error = "Напоминание не найдено." });
        }

        return Results.Ok(new
        {
            snoozed = true,
            dueAt = result?["dueAt"]?.GetValue<string>(),
            dueText = result?["dueText"]?.GetValue<string>(),
        });
    }

    private static IResult firmwareInfoHandler(
        string? current,
        IOptions<KoukiServerOptions> options)
    {
        // мимо команды Mugura: опрос метаданных бинарных образов прошивок станции (OTA), нет состояния домена
        var (status, info) = FirmwareService.GetLatestFirmware(current, options.Value);
        return status switch
        {
            FirmwareCheckStatus.NotConfigured => Results.NotFound(new { error = "firmware-not-configured" }),
            FirmwareCheckStatus.NotFound => Results.NotFound(new { error = "firmware-not-found" }),
            FirmwareCheckStatus.Success when info is not null => Results.Ok(info),
            _ => Results.NotFound(new { error = "firmware-not-found" }),
        };
    }

    private static IResult firmwareImageHandler(
        string? version,
        HttpContext httpContext,
        IOptions<KoukiServerOptions> options)
    {
        // мимо команды Mugura: потоковая отдача бинарного файла прошивки из файловой системы напрямую клиенту, нет логики домена
        var (status, stream, sha256, _) = FirmwareService.TryOpenFile(version, options.Value);
        if (status == FirmwareCheckStatus.NotConfigured)
        {
            return Results.NotFound(new { error = "firmware-not-configured" });
        }

        if (status == FirmwareCheckStatus.NotFound || stream is null)
        {
            return Results.NotFound(new { error = "firmware-not-found" });
        }

        httpContext.Response.Headers.ETag = $"\"{sha256}\"";
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.File(stream, "application/octet-stream", enableRangeProcessing: false);
    }

    private static async Task<IResult> listQuestionsHandler(
        int? limit,
        int? offset,
        string? q,
        IQuestionStore questionStore,
        CancellationToken cancellationToken)
    {
        int take = Math.Clamp(limit ?? 50, 1, 500);
        int skip = Math.Max(0, offset ?? 0);
        IReadOnlyList<QuestionRecord> questions = await questionStore
            .ListAsync(take, skip, q, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(questions);
    }

    private static async Task<IResult> createQuestionTaskHandler(
        Guid id,
        IQuestionStore questionStore,
        MuguraRouter router,
        IContextRegistry contexts,
        CancellationToken cancellationToken)
    {
        QuestionRecord? question = await questionStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (question is null)
        {
            return Results.NotFound(new { error = "Вопрос не найден." });
        }

        if (question.TaskId is Guid existingTaskId)
        {
            JsonNode getResult = await router.RouteAsync(
                "get_task",
                new JsonObject { ["taskId"] = existingTaskId.ToString() },
                contexts.GetDefaultContext(),
                cancellationToken).ConfigureAwait(false);

            if (getResult is JsonObject getPayload && getPayload["found"]?.GetValue<bool>() == true)
            {
                TaskDto? existingTask = ApiMapping.FromTaskJson(getPayload);
                if (existingTask is not null)
                {
                    return Results.Ok(existingTask);
                }
            }
        }

        JsonNode? result = await router.RouteAsync(
            "add_task",
            new JsonObject
            {
                ["text"] = question.Question,
                ["source"] = "question",
            },
            contexts.GetDefaultContext(),
            cancellationToken).ConfigureAwait(false);

        if (result is JsonObject payload
            && payload["taskId"] is JsonValue value
            && value.TryGetValue(out string? textGuid)
            && Guid.TryParse(textGuid, out Guid createdId))
        {
            await questionStore.LinkTaskAsync(id, createdId, cancellationToken).ConfigureAwait(false);
            TaskDto? created = ApiMapping.FromTaskJson(payload);
            return created is null
                ? Results.Created($"/api/tasks/{createdId}", payload)
                : Results.Created($"/api/tasks/{createdId}", created);
        }

        return Results.BadRequest(new { error = "Не удалось создать задачу из вопроса." });
    }
}
