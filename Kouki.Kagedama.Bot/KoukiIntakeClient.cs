using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kouki.Kagedama.Bot;

/// <summary>Задача в ответе сервера.</summary>
public sealed record IntakeTaskDto(Guid Id, string? Title, string? Status, string? Source, string? RawText);

/// <summary>Вердикт распознавания в ответе сервера.</summary>
public sealed record IntakeIntentDto(string Intent, double Score, bool Confident, string? Model);

/// <summary>Ответ <c>POST /api/intake</c>.</summary>
/// <param name="Task">Созданная задача; <c>null</c>, если фраза была командой.</param>
/// <param name="Outcome">Итог: created / executed / needs_review / failed.</param>
/// <param name="Result">Ответ команды (если команда исполнялась).</param>
public sealed record IntakeResponseDto(
    IntakeTaskDto? Task,
    IntakeIntentDto? Intent,
    string? Command,
    bool Executed,
    string? Error,
    string? Outcome = null,
    System.Text.Json.Nodes.JsonNode? Result = null);

/// <summary>Почему обращение к серверу не удалось: по этому выбирается текст ответа человеку.</summary>
public enum IntakeFailure
{
    /// <summary>Ошибки нет.</summary>
    None,

    /// <summary>401/403: сервер не принял токен.</summary>
    Unauthorized,

    /// <summary>Сервер недоступен: сеть, таймаут, отказ в соединении.</summary>
    ServerUnavailable,

    /// <summary>5xx: сервер ответил ошибкой.</summary>
    ServerError,

    /// <summary>400/413/415/422: сервер отказал по существу запроса.</summary>
    BadRequest,

    /// <summary>200, но тело не разобрать.</summary>
    InvalidResponse,
}

/// <summary>Итог отправки фразы в приём задач.</summary>
/// <param name="Ok">Задача принята.</param>
/// <param name="StatusCode">HTTP-код (0 — сеть не дала ответа).</param>
/// <param name="Response">Ответ сервера при успехе.</param>
/// <param name="Failure">Причина отказа при неудаче.</param>
/// <param name="Detail">Подробность для лога (не для человека).</param>
public sealed record IntakeAttempt(
    bool Ok,
    int StatusCode,
    IntakeResponseDto? Response,
    IntakeFailure Failure,
    string? Detail = null)
{
    /// <summary>Успешный приём.</summary>
    public static IntakeAttempt Success(IntakeResponseDto response)
        => new(true, 200, response, IntakeFailure.None);

    /// <summary>Отказ: код и причина.</summary>
    public static IntakeAttempt Fail(int statusCode, IntakeFailure failure, string? detail = null)
        => new(false, statusCode, null, failure, detail);
}

/// <summary>Задача для списка в чате.</summary>
/// <param name="Id">Идентификатор задачи.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Status">Статус (<c>new</c>, <c>in_progress</c>, …).</param>
public sealed record OpenTaskDto(Guid Id, string Title, string Status);

/// <summary>Итог запроса списка задач.</summary>
/// <param name="Ok">Список получен.</param>
/// <param name="StatusCode">HTTP-код.</param>
/// <param name="Tasks">Открытые задачи (свежие сверху).</param>
/// <param name="Failure">Причина отказа.</param>
/// <param name="Detail">Подробность для лога.</param>
public sealed record TaskListAttempt(
    bool Ok,
    int StatusCode,
    IReadOnlyList<OpenTaskDto> Tasks,
    IntakeFailure Failure = IntakeFailure.None,
    string? Detail = null);

/// <summary>Итог команды над задачей (например, «готово»).</summary>
/// <param name="Ok">Команда выполнена.</param>
/// <param name="StatusCode">HTTP-код.</param>
/// <param name="Failure">Причина отказа.</param>
/// <param name="Detail">Подробность для лога.</param>
public sealed record TaskCommandAttempt(
    bool Ok,
    int StatusCode,
    IntakeFailure Failure = IntakeFailure.None,
    string? Detail = null);

/// <summary>Приём фразы сервером Kouki (позволяет подменять транспорт в тестах).</summary>
public interface IKoukiIntake
{
    /// <summary>Отправляет фразу в <c>POST /api/intake</c>.</summary>
    Task<IntakeAttempt> SubmitAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отправляет фразу с подсказкой интента: кнопка «Спросить» не должна
    /// зависеть от того, угадает ли правило слово «сколько».
    /// </summary>
    /// <param name="text">Фраза.</param>
    /// <param name="intent">Интент (<c>ask</c>, <c>add_task</c>) или <c>null</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IntakeAttempt> SubmitAsync(string text, string? intent, CancellationToken cancellationToken = default)
        => SubmitAsync(text, cancellationToken);

    /// <summary>Открытые задачи для кнопок «Готово».</summary>
    /// <param name="limit">Сколько показать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TaskListAttempt> ListOpenTasksAsync(int limit = 5, CancellationToken cancellationToken = default)
        => Task.FromResult(new TaskListAttempt(false, 0, Array.Empty<OpenTaskDto>(), IntakeFailure.ServerUnavailable, "список задач не поддерживается"));

    /// <summary>Закрывает задачу (кнопка «Готово»).</summary>
    /// <param name="id">Задача.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TaskCommandAttempt> CompleteTaskAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(new TaskCommandAttempt(false, 0, IntakeFailure.ServerUnavailable, "закрытие задачи не поддерживается"));

    /// <summary>
    /// Забирает напоминания, у которых наступил срок. Сервер помечает их отправленными
    /// в той же операции, поэтому повторный вызов не пришлёт то же напоминание второй раз.
    /// </summary>
    /// <param name="limit">Сколько забрать за один раз.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<DueReminderAttempt> TakeDueRemindersAsync(int limit = 5, CancellationToken cancellationToken = default)
        => Task.FromResult(new DueReminderAttempt(false, 0, Array.Empty<DueReminderDto>(), IntakeFailure.ServerUnavailable, "напоминания не поддерживаются"));

    /// <summary>Подтверждает напоминание («Готово» под сообщением).</summary>
    /// <param name="id">Напоминание.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TaskCommandAttempt> AcknowledgeReminderAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(new TaskCommandAttempt(false, 0, IntakeFailure.ServerUnavailable, "напоминания не поддерживаются"));

    /// <summary>Откладывает напоминание на несколько минут («+10 минут»).</summary>
    /// <param name="id">Напоминание.</param>
    /// <param name="minutes">На сколько минут отложить.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<TaskCommandAttempt> SnoozeReminderAsync(Guid id, int minutes, CancellationToken cancellationToken = default)
        => Task.FromResult(new TaskCommandAttempt(false, 0, IntakeFailure.ServerUnavailable, "напоминания не поддерживаются"));
}

/// <summary>Напоминание, у которого наступил срок.</summary>
/// <param name="Id">Напоминание.</param>
/// <param name="Text">О чём напомнить.</param>
/// <param name="DueText">Срок по-человечески, как его посчитал сервер.</param>
/// <param name="State">Состояние.</param>
/// <param name="TaskId">Задача-карточка, если напоминание к ней привязано.</param>
public sealed record DueReminderDto(Guid Id, string Text, string DueText, string State, Guid? TaskId = null);

/// <summary>Итог запроса созревших напоминаний.</summary>
/// <param name="Ok">Список получен.</param>
/// <param name="StatusCode">HTTP-код.</param>
/// <param name="Reminders">Созревшие напоминания.</param>
/// <param name="Failure">Причина отказа.</param>
/// <param name="Detail">Подробность для лога.</param>
public sealed record DueReminderAttempt(
    bool Ok,
    int StatusCode,
    IReadOnlyList<DueReminderDto> Reminders,
    IntakeFailure Failure = IntakeFailure.None,
    string? Detail = null);

/// <summary>
/// Клиент сервера Kouki: отправляет текст в приём задач.
/// </summary>
/// <remarks>
/// Ошибки возвращаются значениями, а не исключениями: вызывающему нужно показать
/// человеку «сервер недоступен» или «токен не принят», а <c>ex.Message</c> вида
/// «Response status code does not indicate success: 401» в чат отдавать нельзя.
/// </remarks>
public sealed class KoukiIntakeClient : IKoukiIntake
{
    private const int MaxResponseBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly string _serverToken;
    private readonly string _source;

    /// <summary>
    /// Создаёт клиент.
    /// </summary>
    /// <param name="http">HTTP-клиент с базовым адресом сервера.</param>
    /// <param name="serverToken">Bearer-токен сервера или пустая строка.</param>
    /// <param name="source">Источник задачи.</param>
    public KoukiIntakeClient(HttpClient http, string serverToken, string source)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _serverToken = serverToken ?? string.Empty;
        _source = source ?? "telegram";
    }

    /// <inheritdoc />
    public async Task<IntakeAttempt> SubmitAsync(string text, CancellationToken cancellationToken = default)
        => await SubmitAsync(text, intent: null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IntakeAttempt> SubmitAsync(
        string text,
        string? intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var body = new Dictionary<string, string?>
        {
            ["text"] = text,
            ["source"] = _source,
        };

        if (!string.IsNullOrWhiteSpace(intent))
        {
            body["intent"] = intent;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/intake")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, PayloadOptions), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_serverToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serverToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return IntakeAttempt.Fail(0, IntakeFailure.ServerUnavailable, $"{ex.GetType().Name}: {ex.Message}");
        }

        using (response)
        {
            string json = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return IntakeAttempt.Fail(status, Classify(status), Partial(json));
            }

            try
            {
                IntakeResponseDto? parsed = JsonSerializer.Deserialize<IntakeResponseDto>(json, JsonOptions);
                return parsed is null
                    ? IntakeAttempt.Fail(status, IntakeFailure.InvalidResponse, "пустой ответ")
                    : IntakeAttempt.Success(parsed);
            }
            catch (JsonException ex)
            {
                return IntakeAttempt.Fail(status, IntakeFailure.InvalidResponse, ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public async Task<TaskListAttempt> ListOpenTasksAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(limit, 1, 50);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/tasks?limit={take}");
        AddAuthorization(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new TaskListAttempt(false, 0, Array.Empty<OpenTaskDto>(), IntakeFailure.ServerUnavailable, ex.Message);
        }

        using (response)
        {
            string json = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new TaskListAttempt(false, status, Array.Empty<OpenTaskDto>(), Classify(status), Partial(json));
            }

            try
            {
                List<OpenTaskDto>? tasks = JsonSerializer.Deserialize<List<OpenTaskDto>>(json, JsonOptions);

                // Сервер отдаёт всё по лимиту, а «открытые» — это те, что ещё не закрыты.
                IReadOnlyList<OpenTaskDto> open = (tasks ?? new List<OpenTaskDto>())
                    .Where(t => t.Status is not ("done" or "cancelled"))
                    .Take(take)
                    .ToArray();

                return new TaskListAttempt(true, status, open);
            }
            catch (JsonException ex)
            {
                return new TaskListAttempt(false, status, Array.Empty<OpenTaskDto>(), IntakeFailure.InvalidResponse, ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public async Task<TaskCommandAttempt> CompleteTaskAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, string> { ["status"] = "done" };

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/tasks/{id:D}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, PayloadOptions), Encoding.UTF8, "application/json"),
        };
        AddAuthorization(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new TaskCommandAttempt(false, 0, IntakeFailure.ServerUnavailable, ex.Message);
        }

        using (response)
        {
            string json = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new TaskCommandAttempt(true, status)
                : new TaskCommandAttempt(false, status, Classify(status), Partial(json));
        }
    }

    /// <inheritdoc />
    public async Task<DueReminderAttempt> TakeDueRemindersAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(limit, 1, 50);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/reminders/due?limit={take}");
        AddAuthorization(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new DueReminderAttempt(false, 0, Array.Empty<DueReminderDto>(), IntakeFailure.ServerUnavailable, ex.Message);
        }

        using (response)
        {
            string json = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new DueReminderAttempt(false, status, Array.Empty<DueReminderDto>(), Classify(status), Partial(json));
            }

            try
            {
                List<DueReminderDto>? reminders = JsonSerializer.Deserialize<List<DueReminderDto>>(json, JsonOptions);
                return new DueReminderAttempt(true, status, reminders ?? new List<DueReminderDto>());
            }
            catch (JsonException ex)
            {
                return new DueReminderAttempt(false, status, Array.Empty<DueReminderDto>(), IntakeFailure.InvalidResponse, ex.Message);
            }
        }
    }

    /// <inheritdoc />
    public Task<TaskCommandAttempt> AcknowledgeReminderAsync(Guid id, CancellationToken cancellationToken = default)
        => PostReminderAsync($"api/reminders/{id:D}/ack", cancellationToken);

    /// <inheritdoc />
    public Task<TaskCommandAttempt> SnoozeReminderAsync(Guid id, int minutes, CancellationToken cancellationToken = default)
    {
        int delay = Math.Clamp(minutes, 1, 1440);
        return PostReminderAsync($"api/reminders/{id:D}/snooze?minutes={delay}", cancellationToken);
    }

    /// <summary>POST без тела на маршруты напоминаний: успех — задача выполнена.</summary>
    /// <param name="path">Относительный адрес.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    private async Task<TaskCommandAttempt> PostReminderAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AddAuthorization(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new TaskCommandAttempt(false, 0, IntakeFailure.ServerUnavailable, ex.Message);
        }

        using (response)
        {
            string json = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new TaskCommandAttempt(true, status)
                : new TaskCommandAttempt(false, status, Classify(status), Partial(json));
        }
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_serverToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serverToken);
        }
    }

    private static IntakeFailure Classify(int status) => status switch
    {
        401 or 403 => IntakeFailure.Unauthorized,
        400 or 413 or 415 or 422 => IntakeFailure.BadRequest,
        >= 500 => IntakeFailure.ServerError,
        _ => IntakeFailure.InvalidResponse,
    };

    private static string Partial(string body)
        => body.Length <= 300 ? body : body[..300] + "…";

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];

        while (buffer.Length < MaxResponseBytes)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
