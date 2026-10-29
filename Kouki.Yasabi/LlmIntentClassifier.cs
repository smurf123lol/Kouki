using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kouki.Yasabi;

/// <summary>
/// Интент по языковой модели: короткий JSON вместо рассуждения.
/// </summary>
/// <remarks>
/// Зачем: правила ловят формы, под которые их написали. Модель на 4B параметрах понимает
/// «надо бы не забыть сходить в аптеку» без «запиши задачу» и возвращает интент в том же
/// контракте, что и <see cref="IIntentClassifier"/>, поэтому её можно поставить рядом с
/// правилами и сравнить на одних фразах.
/// </remarks>
public sealed class LlmIntentClassifier : IIntentClassifier
{
    /// <summary>Имя модели в журнале и в сравнении роутеров.</summary>
    public const string ModelName = "llm";

    /// <summary>Интенты по умолчанию: те же, что понимает сервер.</summary>
    public static readonly string[] DefaultIntents = ["add_task", "list_tasks", "complete_task", "add_reminder", "list_reminders", "complete_reminder", "ask", "chat"];

    private static readonly JsonSerializerOptions Serializer = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly LlmIntentOptions _options;

    /// <summary>Создаёт классификатор поверх OpenAI-совместимого API.</summary>
    /// <param name="http">Клиент HTTP; время ожидания задаётся настройками.</param>
    /// <param name="options">Настройки модели.</param>
    public LlmIntentClassifier(HttpClient http, LlmIntentOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120));
    }

    /// <inheritdoc />
    public async Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return IntentResult.Unknown(ModelName);
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri())
            {
                Content = new StringContent(BuildPayload(text), Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return IntentResult.Unknown(ModelName);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(body, AllowedIntents());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Модель недоступна или ответила мусором — молча «не понял»: приём не должен падать
            // из-за классификатора, у него есть правила и человек в дашборде.
            return IntentResult.Unknown(ModelName);
        }
    }

    /// <summary>Разбирает ответ OpenAI-совместимого API в вердикт.</summary>
    /// <param name="body">Тело ответа.</param>
    /// <param name="allowed">Интенты, которые принимаются.</param>
    /// <returns>Вердикт или «не понял», если ответ не по формату.</returns>
    public static IntentResult Parse(string body, IReadOnlyCollection<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allowed);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return IntentResult.Unknown(ModelName);
        }

        var content = choices.EnumerateArray()
            .Select(static choice => choice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));

        if (content is null)
        {
            return IntentResult.Unknown(ModelName);
        }

        var json = ExtractObject(content);
        if (json is null)
        {
            return IntentResult.Unknown(ModelName);
        }

        var intent = json["intent"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var confidence = json["confidence"]?.GetValue<double>() ?? 0d;

        if (string.IsNullOrWhiteSpace(intent)
            || intent == IntentResult.Unclassified
            || !allowed.Contains(intent, StringComparer.OrdinalIgnoreCase))
        {
            return IntentResult.Unknown(ModelName);
        }

        return new IntentResult(intent, Math.Clamp(confidence, 0d, 1d), null, ModelName);
    }

    /// <summary>Промпт: короткий ответ в JSON, без рассуждений.</summary>
    /// <param name="intents">Допустимые интенты.</param>
    /// <returns>Системное сообщение.</returns>
    public static string BuildSystemPrompt(IEnumerable<string> intents)
    {
        var list = string.Join(", ", intents);
        return $"Ты классифицируешь фразу в один интент. Ответь одним JSON-объектом без пояснений: " +
               $"{{\"intent\": \"...\", \"confidence\": 0.0}}. Допустимые интенты: {list}, unknown. " +
               "add_task — просьба сохранить дело или задачу («запиши задачу», «надо не забыть», «купи»); " +
               "list_tasks — показать задачи; complete_task — отметить задачу выполненной; " +
               "add_reminder — просьба напомнить, и только если во фразе есть время или срок " +
               "(«через 20 минут», «завтра в 9», «каждый день»); без времени это add_task; " +
               "list_reminders — показать напоминания; complete_reminder — закрыть напоминание; " +
               "ask — вопрос, ответ на который нужно найти; chat — разговор или благодарность без команды; " +
               "unknown — если не подходит ничего. " +
               "confidence — уверенность от 0 до 1. Отвечай только JSON.";
    }

    /// <summary>Тело запроса к модели.</summary>
    /// <param name="text">Фраза пользователя.</param>
    /// <returns>JSON тела.</returns>
    public string BuildPayload(string text)
    {
        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = 0,
            ["max_tokens"] = Math.Clamp(_options.MaxOutputTokens, 1, 256),
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt(AllowedIntents()) },
                new JsonObject { ["role"] = "user", ["content"] = text },
            },
        };

        return payload.ToJsonString(Serializer);
    }

    private string BuildUri()
    {
        var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "http://127.0.0.1:8080/v1" : _options.Endpoint.TrimEnd('/');
        return endpoint + "/chat/completions";
    }

    private string[] AllowedIntents()
        => _options.Intents is { Length: > 0 } intents ? intents : DefaultIntents;

    private static JsonObject? ExtractObject(string content)
    {
        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}');

        // Модель иногда оборачивает JSON в ```json … ``` или в текст — берём первую пару скобок.
        return start >= 0 && end > start && JsonNode.Parse(content[start..(end + 1)]) is JsonObject obj ? obj : null;
    }
}
