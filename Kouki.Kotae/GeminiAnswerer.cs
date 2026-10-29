using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kotae;

/// <summary>
/// Ответчик через Gemini API (Google AI Studio).
/// </summary>
/// <remarks>
/// Запрос простой: <c>POST {Endpoint}/models/{Model}:generateContent</c> с заголовком
/// <c>x-goog-api-key</c>. Ответ берём из <c>candidates[0].content.parts[*].text</c>;
/// если Google отказал (<c>error.message</c>) или заблокировал запрос
/// (<c>promptFeedback.blockReason</c>), бросаем <see cref="AnswererException"/> с текстом.
/// </remarks>
public sealed class GeminiAnswerer : IAnswerer
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly KotaeOptions _options;
    private readonly string? _problem;

    /// <summary>Создаёт ответчик по настройкам.</summary>
    public GeminiAnswerer(KotaeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _problem = options.Validate();
    }

    /// <inheritdoc />
    public bool IsConfigured => _problem is null;

    /// <inheritdoc />
    public string? UnavailableReason => _problem;

    /// <inheritdoc />
    public string Signature => $"gemini:{_options.Model}";

    /// <inheritdoc />
    public async Task<Answer> AnswerAsync(string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        if (!IsConfigured)
        {
            throw new AnswererException(_problem!);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var url = $"{_options.Endpoint.TrimEnd('/')}/models/{Uri.EscapeDataString(_options.Model)}:generateContent";
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(BuildBody(question), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AnswererException($"модель не ответила за {_options.TimeoutSeconds} с");
        }
        catch (HttpRequestException ex)
        {
            throw new AnswererException($"нет связи с Gemini API: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new AnswererException(DescribeFailure(response.StatusCode, body));
            }

            var text = ExtractText(body);

            stopwatch.Stop();
            return new Answer(text, Signature, stopwatch.ElapsedMilliseconds);
        }
    }

    private string BuildBody(string question)
    {
        var payload = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = _options.SystemPrompt }),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = question }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = _options.MaxOutputTokens,
                ["temperature"] = 0.4,
            },
        };

        return payload.ToJsonString();
    }

    private static string ExtractText(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AnswererException("Gemini API вернул не JSON", ex);
        }

        if (root?["candidates"] is not JsonArray candidates || candidates.Count == 0)
        {
            var blockReason = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(blockReason))
            {
                throw new AnswererException($"запрос заблокирован фильтром Gemini ({blockReason})");
            }

            throw new AnswererException("Gemini API не вернул ни одного варианта ответа");
        }

        var builder = new StringBuilder();
        foreach (var part in candidates[0]?["content"]?["parts"] as JsonArray ?? new JsonArray())
        {
            var text = part?["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(text.Trim());
            }
        }

        var finish = candidates[0]?["finishReason"]?.GetValue<string>();
        if (builder.Length == 0)
        {
            throw new AnswererException($"модель вернула пустой ответ (finishReason: {finish ?? "нет"})");
        }

        return builder.ToString();
    }

    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        var message = TryReadErrorMessage(body);

        return status switch
        {
            HttpStatusCode.BadRequest => $"Gemini отклонил запрос: {message}",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"ключ не принят: {message}",
            HttpStatusCode.TooManyRequests => $"лимит запросов исчерпан: {message}",
            HttpStatusCode.NotFound => $"модель не найдена: {message}",
            HttpStatusCode.ServiceUnavailable => $"Gemini недоступен: {message}",
            _ => $"Gemini ответил {(int)status}: {message}",
        };
    }

    private static string TryReadErrorMessage(string body)
    {
        try
        {
            var message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(message) ? "без пояснения" : message.Trim();
        }
        catch (JsonException)
        {
            return "без пояснения";
        }
    }
}
