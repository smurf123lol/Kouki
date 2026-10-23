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

/// <summary>
/// Клиент сервера Kouki: отправляет текст в приём задач.
/// </summary>
public sealed class KoukiIntakeClient
{
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

    /// <summary>
    /// Отправляет фразу в приём задач.
    /// </summary>
    /// <param name="text">Текст задачи.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<IntakeResponseDto> SubmitAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var body = new Dictionary<string, string?>
        {
            ["text"] = text,
            ["source"] = _source,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/intake")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, PayloadOptions), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_serverToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serverToken);
        }

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kouki ответил {(int)response.StatusCode}: {json}");
        }

        return JsonSerializer.Deserialize<IntakeResponseDto>(json, JsonOptions)
               ?? throw new InvalidOperationException("Kouki вернул пустой ответ.");
    }
}
