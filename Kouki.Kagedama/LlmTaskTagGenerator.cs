using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kouki.Kagedama;

/// <summary>
/// Извлечение тегов задачи через локальную LLM (OpenAI-совместимый эндпоинт).
/// </summary>
public sealed class LlmTaskTagGenerator : ITaskTagGenerator
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;

    public LlmTaskTagGenerator(
        HttpClient http,
        string endpoint = "http://192.168.1.157:8080/v1/chat/completions",
        string model = "qwen3")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = NormalizeEndpoint(endpoint);
        _model = string.IsNullOrWhiteSpace(model) ? "qwen3" : model;
    }

    public async Task<IReadOnlyList<string>> GenerateTagsAsync(string title, string rawText, CancellationToken cancellationToken = default)
    {
        string phrase = !string.IsNullOrWhiteSpace(title) ? title : rawText;
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            var payload = new JsonObject
            {
                ["model"] = _model,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = "Ты извлекаешь ключевые теги для задачи. Ответь ТОЛЬКО валидным JSON-массивом из 1-4 коротких слов (существительные в именительном падеже, нижний регистр, без знака #), например [\"покупки\", \"магазин\"]. Если фраза не содержит ясной темы, верни []."
                    },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = phrase
                    }
                },
                ["max_tokens"] = 35,
                ["temperature"] = 0.1,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentElem))
                {
                    string content = (contentElem.GetString() ?? string.Empty).Trim();
                    return ParseTags(content);
                }
            }
        }
        catch
        {
            // Сбой сети, таймаут или парсинг — отдаём пустой список тегов без прерывания
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ParseTags(string content)
    {
        int start = content.IndexOf('[');
        int end = content.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return Array.Empty<string>();
        }

        try
        {
            string arrayJson = content.Substring(start, end - start + 1);
            using var arrayDoc = JsonDocument.Parse(arrayJson);
            if (arrayDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in arrayDoc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? tag = item.GetString()?.Trim().Trim('#', ' ', '"', '\'');
                        if (!string.IsNullOrWhiteSpace(tag))
                        {
                            list.Add(tag.ToLowerInvariant());
                        }
                    }
                }
                return list;
            }
        }
        catch
        {
        }

        return Array.Empty<string>();
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        string trimmed = string.IsNullOrWhiteSpace(endpoint) ? "http://192.168.1.157:8080/v1" : endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }
}
