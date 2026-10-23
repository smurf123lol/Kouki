using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
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

        string source = string.IsNullOrWhiteSpace(request.Source) ? "api" : request.Source.Trim();
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

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > settings.MaxVoiceBytes)
            {
                return Results.Json(
                    new { error = $"Запись больше {settings.MaxVoiceBytes} байт." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        byte[] bytes = buffer.ToArray();

        if (bytes.Length == 0)
        {
            return Results.BadRequest(new { error = "Пустое тело запроса: ожидался WAV." });
        }

        if (!WavReader.TryParse(bytes, out WavAudio? audio, out string? wavError) || audio is null)
        {
            return Results.Json(new { error = wavError }, statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        if (audio.Duration > TimeSpan.FromSeconds(settings.MaxVoiceSeconds))
        {
            return Results.Json(
                new { error = $"Запись длиннее {settings.MaxVoiceSeconds} с." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
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

    private static async Task<IResult> listTasksHandler(
        string? status,
        int? limit,
        ITaskStore store,
        CancellationToken cancellationToken)
    {
        if (status is not null && !TaskStatuses.IsKnown(status))
        {
            return Results.BadRequest(new { error = $"Неизвестный статус '{status}'." });
        }

        int take = Math.Clamp(limit ?? 50, 1, 500);
        IReadOnlyList<TaskRecord> tasks = await store.ListAsync(status, take, cancellationToken);

        return Results.Ok(tasks.Select(ApiMapping.ToDto).ToList());
    }

    private static async Task<IResult> getTaskHandler(Guid id, ITaskStore store, CancellationToken cancellationToken)
    {
        TaskRecord? task = await store.GetAsync(id, cancellationToken);
        return task is null ? Results.NotFound(new { error = "Задача не найдена." }) : Results.Ok(ApiMapping.ToDto(task));
    }

    private static async Task<IResult> patchTaskHandler(
        Guid id,
        TaskPatchRequest request,
        ITaskStore store,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Тело запроса обязательно." });
        }

        if (request.Status is not null && !TaskStatuses.IsKnown(request.Status))
        {
            return Results.BadRequest(new { error = $"Неизвестный статус '{request.Status}'." });
        }

        if (request.Priority is < 1 or > 3)
        {
            return Results.BadRequest(new { error = "Приоритет: 1, 2 или 3." });
        }

        bool updated = await store.UpdateAsync(
            id, request.Status, request.Title, request.Priority, cancellationToken: cancellationToken);
        if (!updated)
        {
            return Results.NotFound(new { error = "Задача не найдена." });
        }

        TaskRecord? task = await store.GetAsync(id, cancellationToken);
        return task is null ? Results.NotFound(new { error = "Задача не найдена." }) : Results.Ok(ApiMapping.ToDto(task));
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
