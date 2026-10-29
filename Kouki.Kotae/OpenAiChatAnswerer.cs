using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kotae;

/// <summary>
/// Ответчик через OpenAI-совместимый API: DeepSeek (<c>/v1</c>), Mistral, OpenRouter,
/// локальный шлюз. Протокол один — <c>POST {Endpoint}/chat/completions</c>
/// с <c>Authorization: Bearer</c> и <c>{model, messages[], max_tokens, temperature}</c>.
/// </summary>
/// <remarks>
/// Взят вместо Gemini не из любви к OpenAI: Gemini API отказывает серверу по региону
/// («User location is not supported for the API use»), а DeepSeek отвечает из России
/// без прокси и стоит копейки. Отказ провайдера превращаем в
/// <see cref="AnswererException"/> с человеческим текстом.
/// </remarks>
public sealed class OpenAiChatAnswerer : IAnswerer
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly KotaeOptions _options;
    private readonly string? _problem;

    /// <summary>Создаёт ответчик по настройкам.</summary>
    public OpenAiChatAnswerer(KotaeOptions options)
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
    public string Signature => $"openai:{_options.ResolveModel()}";

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

        var url = $"{_options.ResolveEndpoint()}/chat/completions";
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(BuildBody(question), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

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
            throw new AnswererException($"нет связи с {new Uri(url).Host}: {ex.Message}", ex);
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

    private string BuildBody(string question) => BuildPayload(_options, question).ToJsonString();

    /// <summary>Собирает тело запроса <c>/chat/completions</c> (отдельно — чтобы покрыть тестом).</summary>
    public static JsonObject BuildPayload(KotaeOptions options, string question)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var payload = new JsonObject
        {
            ["model"] = options.ResolveModel(),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = options.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = question }),
            ["max_tokens"] = options.MaxOutputTokens,
            ["temperature"] = 0.4,
            ["stream"] = false,
        };

        if (options.DisableThinking)
        {
            // llama.cpp отдаёт эти ключи шаблону чата модели: выключает размышление
            // и включает «разговорный» режим — ответ сразу короткой фразой.
            payload["chat_template_kwargs"] = new JsonObject
            {
                ["enable_thinking"] = false,
                ["conversational_mode"] = true,
            };
        }

        return payload;
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
            throw new AnswererException("провайдер вернул не JSON", ex);
        }

        var content = root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(content))
        {
            return content.Trim();
        }

        var reasoning = root?["choices"]?[0]?["message"]?["reasoning_content"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            throw new AnswererException(
                "модель только рассуждала и не дала ответа — включите Kouki:Answerer:DisableThinking");
        }

        var finish = root?["choices"]?[0]?["finish_reason"]?.GetValue<string>();
        throw new AnswererException($"модель вернула пустой ответ (finish_reason: {finish ?? "нет"})");
    }

    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        var message = TryReadErrorMessage(body);

        return status switch
        {
            HttpStatusCode.BadRequest => $"провайдер отклонил запрос: {message}",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"ключ не принят: {message}",
            HttpStatusCode.PaymentRequired => $"на счету провайдера нет денег: {message}",
            HttpStatusCode.TooManyRequests => $"лимит запросов исчерпан: {message}",
            HttpStatusCode.NotFound => $"модель или адрес не найдены: {message}",
            HttpStatusCode.ServiceUnavailable => $"провайдер недоступен: {message}",
            _ => $"провайдер ответил {(int)status}: {message}",
        };
    }

    private static string TryReadErrorMessage(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            var message = root?["error"]?["message"]?.GetValue<string>()
                ?? root?["message"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(message) ? "без пояснения" : message.Trim();
        }
        catch (JsonException)
        {
            return "без пояснения";
        }
    }
}
