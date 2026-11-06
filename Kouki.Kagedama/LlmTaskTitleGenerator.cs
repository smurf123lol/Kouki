using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kouki.Kagedama;

/// <summary>
/// Генератор заголовка на языковой модели с откатом на правила.
/// </summary>
public sealed class LlmTaskTitleGenerator : ITaskTitleGenerator
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly ITaskTitleGenerator _fallback;

    public LlmTaskTitleGenerator(
        HttpClient http,
        string endpoint = "http://192.168.1.157:8080/v1/chat/completions",
        string model = "qwen3",
        ITaskTitleGenerator? fallback = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = NormalizeEndpoint(endpoint);
        _model = string.IsNullOrWhiteSpace(model) ? "qwen3" : model;
        _fallback = fallback ?? RuleTaskTitleGenerator.Instance;
    }

    /// <inheritdoc />
    public async Task<string> GenerateTitleAsync(string rawText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        string trimmed = rawText.Trim();

        // Очень короткие фразы (до 4 слов) правила очищают мгновенно и без вызова LLM
        if (trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4)
        {
            return await _fallback.GenerateTitleAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            var payload = new JsonObject
            {
                ["model"] = _model,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = "Ты формулируешь краткий заголовок задачи из фразы пользователя на русском языке. Убери слова вроде 'запиши', 'добавь', 'сохрани', 'надо'. Напиши ТОЛЬКО заголовок, с заглавной буквы, до 60 символов, без кавычек и точки в конце."
                    },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = trimmed
                    }
                },
                ["max_tokens"] = 25,
                ["temperature"] = 0.1
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await _fallback.GenerateTitleAsync(trimmed, cancellationToken).ConfigureAwait(false);
            }

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentElem))
                {
                    string candidate = (contentElem.GetString() ?? string.Empty).Trim().Trim('"', '\'', '`');
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        if (char.IsLower(candidate[0]))
                        {
                            candidate = char.ToUpper(candidate[0]) + candidate[1..];
                        }
                        return TaskTitles.Make(candidate);
                    }
                }
            }
        }
        catch
        {
            // Сбой сети или таймаут — откат на правила
        }

        return await _fallback.GenerateTitleAsync(trimmed, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        string trimmed = string.IsNullOrWhiteSpace(endpoint) ? "http://192.168.1.157:8080/v1" : endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }
}
