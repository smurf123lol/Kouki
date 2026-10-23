using System.Net;
using System.Text;
using System.Text.Json;
using Kouki.Kagedama.Bot;

namespace Kouki.Tests;

/// <summary>Проверки Telegram-бота: разбор обновлений, клиент Kouki, текст ответа.</summary>
public sealed class TelegramBotTests
{
    [Fact]
    public void Updates_are_parsed_into_text_and_chat()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "ok": true,
              "result": [
                { "update_id": 10, "message": { "message_id": 1, "chat": { "id": 555 }, "text": "купить хлеб" } },
                { "update_id": 11, "edited_message": { "chat": { "id": 556 } } },
                { "update_id": 12, "message": { "message_id": 2, "chat": { "id": 777 }, "photo": [] } }
              ]
            }
            """);

        IReadOnlyList<TelegramMessage> messages = TelegramApiClient.ParseUpdates(document.RootElement);

        Assert.Equal(2, messages.Count);
        Assert.Equal(10, messages[0].UpdateId);
        Assert.Equal(555, messages[0].ChatId);
        Assert.Equal("купить хлеб", messages[0].Text);
        Assert.Equal(12, messages[1].UpdateId);
        Assert.Equal(string.Empty, messages[1].Text);
    }

    [Fact]
    public void Failed_telegram_response_yields_no_messages()
    {
        using JsonDocument document = JsonDocument.Parse("""{ "ok": false, "description": "Unauthorized" }""");

        Assert.Empty(TelegramApiClient.ParseUpdates(document.RootElement));
    }

    [Fact]
    public async Task Intake_client_sends_text_with_bearer_token()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {
              "task": { "id": "7f1d0f7c-6b47-4d6a-9a1f-5ce0b7f1a111", "title": "купить хлеб", "status": "new" },
              "intent": { "intent": "add_task", "score": 1.0, "confident": true, "model": "rules-v1" },
              "command": null,
              "executed": false,
              "error": null
            }
            """));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://kouki.test/") };
        var client = new KoukiIntakeClient(http, "secret", "telegram");

        IntakeResponseDto response = await client.SubmitAsync("купить хлеб");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://kouki.test/api/intake", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("secret", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("купить хлеб", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"telegram\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Equal("add_task", response.Intent!.Intent);
        Assert.Equal("new", response.Task!.Status);
    }

    [Fact]
    public async Task Intake_client_reports_server_error()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{ "error": "unauthorized" }"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://kouki.test/") };
        var client = new KoukiIntakeClient(http, string.Empty, "telegram");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitAsync("купить хлеб"));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public void Reply_marks_review_status()
    {
        var response = new IntakeResponseDto(
            new IntakeTaskDto(Guid.NewGuid(), "погода сегодня хорошая", "needs_review", "telegram", null),
            new IntakeIntentDto("unclassified", 0d, Confident: false, "rules-v1"),
            Command: null,
            Executed: false,
            Error: null);

        string reply = IntakeReplyFormatter.Format(response);

        Assert.Contains("на разборе", reply, StringComparison.Ordinal);
        Assert.Contains("неуверенно", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_lists_command_for_executed_task()
    {
        var response = new IntakeResponseDto(
            new IntakeTaskDto(Guid.NewGuid(), "покажи мои задачи", "done", "telegram", null),
            new IntakeIntentDto("list_tasks", 1d, Confident: true, "rules-v1"),
            Command: "list_tasks",
            Executed: true,
            Error: null);

        string reply = IntakeReplyFormatter.Format(response);

        Assert.Contains("выполнена", reply, StringComparison.Ordinal);
        Assert.Contains("list_tasks", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("неуверенно", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_are_overridden_by_arguments()
    {
        BotOptions options = BotOptions.Load(new[]
        {
            "--bot-token=123:abc",
            "--server-url=http://192.168.1.10:5199",
            "--server-token=secret",
        });

        Assert.Equal("123:abc", options.BotToken);
        Assert.Equal("http://192.168.1.10:5199", options.ServerUrl);
        Assert.Equal("secret", options.ServerToken);
    }

    [Fact]
    public void Command_only_response_is_rendered_without_task()
    {
        var result = new System.Text.Json.Nodes.JsonObject
        {
            ["count"] = 2,
            ["tasks"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["id"] = Guid.NewGuid().ToString(), ["title"] = "купить хлеб", ["status"] = "new" },
                new System.Text.Json.Nodes.JsonObject { ["id"] = Guid.NewGuid().ToString(), ["title"] = "позвонить маме", ["status"] = "done" },
            },
        };

        var response = new IntakeResponseDto(
            Task: null,
            new IntakeIntentDto("list_tasks", 1d, Confident: true, "rules-v1"),
            Command: "list_tasks",
            Executed: true,
            Error: null,
            Outcome: "executed",
            Result: result);

        string reply = IntakeReplyFormatter.Format(response);

        Assert.Contains("Задачи (2)", reply, StringComparison.Ordinal);
        Assert.Contains("купить хлеб — новая", reply, StringComparison.Ordinal);
        Assert.Contains("позвонить маме — выполнена", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Задача:", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_error_is_reported_in_reply()
    {
        var response = new IntakeResponseDto(
            Task: null,
            new IntakeIntentDto("complete_task", 1d, Confident: true, "rules-v1"),
            Command: "complete_task",
            Executed: false,
            Error: null,
            Outcome: "failed",
            Result: new System.Text.Json.Nodes.JsonObject { ["error"] = "Задача 7 не найдена" });

        string reply = IntakeReplyFormatter.Format(response);

        Assert.Contains("Не вышло", reply, StringComparison.Ordinal);
        Assert.Contains("не найдена", reply, StringComparison.Ordinal);
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return _responder(request);
        }
    }
}
