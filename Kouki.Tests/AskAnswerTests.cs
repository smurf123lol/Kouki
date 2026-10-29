using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kotae;

namespace Kouki.Tests;

/// <summary>
/// Команда <c>ask</c>: маршрут «вопрос → команда → ответ» один, меняется только
/// источник ответа. Проверяем три состояния — нет ответчика, модель ответила,
/// модель отказала: отказ не должен выглядеть как пустой ответ и не должен
/// ронять команду исключением.
/// </summary>
public sealed class AskAnswerTests
{
    private sealed class FakeAnswerer(string? text = null, Exception? failure = null) : IAnswerer
    {
        public bool IsConfigured => true;

        public string? UnavailableReason => null;

        public string Signature => "fake:test";

        public Task<Answer> AnswerAsync(string question, CancellationToken cancellationToken = default)
            => failure is null
                ? Task.FromResult(new Answer(text ?? "ответ", Signature, 12))
                : Task.FromException<Answer>(failure);
    }

    [Fact]
    public async Task Without_an_answerer_the_command_answers_with_the_stub()
    {
        var handler = new AskHandler();
        var payload = await handler.HandleAsync(new AskCommand("Сколько минут варить пельмени?"));

        Assert.Equal(AskHandler.StubAnswer, payload["answer"]!.GetValue<string>());
        Assert.True(payload["stub"]!.GetValue<bool>());
        Assert.Null(payload["stubReason"]);
    }

    [Fact]
    public async Task A_disabled_answerer_explains_itself()
    {
        var handler = new AskHandler(new DisabledAnswerer("не задан ключ: Kouki:Answerer:ApiKey"));
        var payload = await handler.HandleAsync(new AskCommand("Что такое SQLite?"));

        Assert.Equal(AskHandler.StubAnswer, payload["answer"]!.GetValue<string>());
        Assert.Equal("не задан ключ: Kouki:Answerer:ApiKey", payload["stubReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_model_answer_replaces_the_stub_and_carries_the_signature()
    {
        var handler = new AskHandler(new FakeAnswerer("Куриное яйцо варится 9 минут."));
        var payload = await handler.HandleAsync(new AskCommand("  Сколько варить яйцо?  "));

        Assert.Equal("Куриное яйцо варится 9 минут.", payload["answer"]!.GetValue<string>());
        Assert.False(payload["stub"]!.GetValue<bool>());
        Assert.Equal("fake:test", payload["answerer"]!.GetValue<string>());
        Assert.Equal(12L, payload["answerMs"]!.GetValue<long>());
        Assert.Null(payload["error"]);
    }

    [Fact]
    public async Task A_model_failure_is_reported_as_an_answer_not_an_exception()
    {
        var handler = new AskHandler(new FakeAnswerer(failure: new AnswererException("лимит запросов исчерпан")));
        var payload = await handler.HandleAsync(new AskCommand("Что нового в .NET 10?"));

        Assert.Equal("Ответить не получилось: лимит запросов исчерпан", payload["answer"]!.GetValue<string>());
        Assert.Equal("лимит запросов исчерпан", payload["error"]!.GetValue<string>());
        Assert.False(payload["stub"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_empty_question_never_reaches_the_model()
    {
        var handler = new AskHandler(new FakeAnswerer("не должно случиться"));
        var payload = await handler.HandleAsync(new AskCommand("   "));

        Assert.Equal(AskHandler.StubAnswer, payload["answer"]!.GetValue<string>());
        Assert.True(payload["stub"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_task_id_from_intake_is_echoed_back()
    {
        var handler = new AskHandler(new FakeAnswerer("девять минут"));
        var taskId = Guid.NewGuid().ToString();
        var payload = await handler.HandleAsync(new AskCommand("Сколько варить яйцо?", taskId));

        Assert.Equal(taskId, payload["taskId"]!.GetValue<string>());
        Assert.Equal("девять минут", payload["answer"]!.GetValue<string>());
    }

    [Fact]
    public void Disabled_provider_explains_why()
    {
        string? problem = new KotaeOptions().Validate();

        Assert.NotNull(problem);
        Assert.Contains("Provider", problem);
    }

    [Fact]
    public void A_configured_gemini_answerer_has_no_complaints()
    {
        var options = new KotaeOptions
        {
            Provider = KotaeProvider.Gemini,
            ApiKey = "test-key",
        };

        Assert.Null(options.Validate());
    }

    [Fact]
    public void Missing_key_is_reported()
    {
        var options = new KotaeOptions { Provider = KotaeProvider.Gemini };

        Assert.Contains("ApiKey", options.Validate());
    }

    [Fact]
    public void Non_https_endpoint_is_rejected()
    {
        var options = new KotaeOptions
        {
            Provider = KotaeProvider.Gemini,
            ApiKey = "test-key",
            Endpoint = "http://generativelanguage.googleapis.com/v1beta",
        };

        Assert.Contains("https", options.Validate());
    }

    [Fact]
    public void OpenAi_provider_falls_back_to_deepseek_defaults()
    {
        var options = new KotaeOptions
        {
            Provider = KotaeProvider.OpenAi,
            ApiKey = "test-key",
        };

        Assert.Null(options.Validate());
        Assert.Equal("deepseek-chat", options.ResolveModel());
        Assert.Equal("https://api.deepseek.com/v1", options.ResolveEndpoint());
        Assert.Equal("openai:deepseek-chat", new OpenAiChatAnswerer(options).Signature);
    }

    [Fact]
    public void A_trailing_slash_in_the_endpoint_does_not_double_up()
    {
        var options = new KotaeOptions
        {
            Provider = KotaeProvider.OpenAi,
            ApiKey = "test-key",
            Endpoint = "https://api.mistral.ai/v1/",
            Model = "mistral-small-latest",
        };

        Assert.Null(options.Validate());
        Assert.Equal("https://api.mistral.ai/v1", options.ResolveEndpoint());
        Assert.Equal("mistral-small-latest", options.ResolveModel());
    }

    [Fact]
    public void An_unknown_provider_number_is_reported()
    {
        var options = new KotaeOptions
        {
            Provider = (KotaeProvider)99,
            ApiKey = "test-key",
        };

        Assert.Contains("неизвестный провайдер", options.Validate());
    }

    [Fact]
    public void Gemini_keeps_its_own_default_endpoint()
    {
        var options = new KotaeOptions
        {
            Provider = KotaeProvider.Gemini,
            ApiKey = "test-key",
        };

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta", options.ResolveEndpoint());
        Assert.StartsWith("gemini-", options.ResolveModel());
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("http://192.168.1.157:8080/v1")]
    [InlineData("http://10.0.0.5:8080/v1")]
    public void A_local_model_may_speak_plain_http(string endpoint)
    {
        var options = new KotaeOptions
        {
            Provider = KotaeProvider.OpenAi,
            ApiKey = "local",
            Endpoint = endpoint,
            Model = "nemotron",
        };

        Assert.Null(options.Validate());
    }

    [Fact]
    public void A_remote_answerer_must_use_https()
    {
        var options = new KotaeOptions
        {
            Provider = KotaeProvider.OpenAi,
            ApiKey = "test-key",
            Endpoint = "http://api.example.com/v1",
        };

        Assert.Contains("https", options.Validate());
    }

    [Fact]
    public void Thinking_is_off_only_when_asked()
    {
        var quiet = new KotaeOptions { Provider = KotaeProvider.OpenAi, ApiKey = "local", DisableThinking = true };
        var loud = new KotaeOptions { Provider = KotaeProvider.OpenAi, ApiKey = "local" };

        var quietBody = OpenAiChatAnswerer.BuildPayload(quiet, "Сколько варить яйцо?");
        var loudBody = OpenAiChatAnswerer.BuildPayload(loud, "Сколько варить яйцо?");

        Assert.False(quietBody["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.True(quietBody["chat_template_kwargs"]!["conversational_mode"]!.GetValue<bool>());
        Assert.Null(loudBody["chat_template_kwargs"]);
    }
}
