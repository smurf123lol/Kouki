using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Bot;

namespace Kouki.Tests;

/// <summary>Проверки Telegram-бота: разбор обновлений, клиенты, тексты ответов, цикл.</summary>
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

        IntakeAttempt attempt = await client.SubmitAsync("купить хлеб");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://kouki.test/api/intake", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("secret", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("купить хлеб", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"telegram\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.True(attempt.Ok);
        Assert.Equal("add_task", attempt.Response!.Intent!.Intent);
        Assert.Equal("new", attempt.Response.Task!.Status);
    }

    [Fact]
    public async Task Intake_client_maps_unauthorized_without_throwing()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{ "error": "unauthorized" }"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://kouki.test/") };
        var client = new KoukiIntakeClient(http, string.Empty, "telegram");

        IntakeAttempt attempt = await client.SubmitAsync("купить хлеб");

        Assert.False(attempt.Ok);
        Assert.Equal(401, attempt.StatusCode);
        Assert.Equal(IntakeFailure.Unauthorized, attempt.Failure);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Theory]
    [InlineData(500, IntakeFailure.ServerError)]
    [InlineData(503, IntakeFailure.ServerError)]
    [InlineData(400, IntakeFailure.BadRequest)]
    [InlineData(422, IntakeFailure.BadRequest)]
    [InlineData(429, IntakeFailure.InvalidResponse)]
    public async Task Intake_client_classifies_http_failures(int status, IntakeFailure expected)
    {
        var handler = new StubHandler(_ => Json((HttpStatusCode)status, """{ "error": "nope" }"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://kouki.test/") };
        var client = new KoukiIntakeClient(http, "secret", "telegram");

        IntakeAttempt attempt = await client.SubmitAsync("купить хлеб");

        Assert.False(attempt.Ok);
        Assert.Equal(status, attempt.StatusCode);
        Assert.Equal(expected, attempt.Failure);
    }

    [Fact]
    public async Task Intake_client_reports_unavailable_when_network_fails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("нет соединения"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://kouki.test/") };
        var client = new KoukiIntakeClient(http, "secret", "telegram");

        IntakeAttempt attempt = await client.SubmitAsync("купить хлеб");

        Assert.False(attempt.Ok);
        Assert.Equal(0, attempt.StatusCode);
        Assert.Equal(IntakeFailure.ServerUnavailable, attempt.Failure);
    }

    [Fact]
    public async Task Telegram_429_is_reported_with_retry_after()
    {
        var handler = new StubHandler(_ => Json(
            (HttpStatusCode)429,
            """{ "ok": false, "error_code": 429, "description": "Too Many Requests: retry after 7", "parameters": { "retry_after": 7 } }"""));

        using var http = new HttpClient(handler);
        var client = new TelegramApiClient(http, "123:abc", "http://telegram.test");

        TelegramUpdatesResult result = await client.GetUpdatesAsync(0, 25);

        Assert.False(result.Ok);
        Assert.True(result.IsRateLimited);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
        Assert.Contains("retry after 7", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_409_is_a_conflict_and_webhook_info_is_parsed()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/getWebhookInfo", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{ "ok": true, "result": { "url": "https://example.org/hook", "pending_update_count": 3 } }""")
            : Json((HttpStatusCode)409, """{ "ok": false, "error_code": 409, "description": "Conflict: terminated by other getUpdates request" }"""));

        using var http = new HttpClient(handler);
        var client = new TelegramApiClient(http, "123:abc", "http://telegram.test/");

        TelegramUpdatesResult updates = await client.GetUpdatesAsync(0, 25);
        TelegramWebhookInfo webhook = await client.GetWebhookInfoAsync();

        Assert.True(updates.IsConflict);
        Assert.False(updates.IsUnauthorized);
        Assert.True(webhook.Ok);
        Assert.True(webhook.HasWebhook);
        Assert.Equal("https://example.org/hook", webhook.Url);
        Assert.Equal(3, webhook.PendingUpdateCount);
        // Токен в пути, базовый адрес — из конструктора, без склейки строк.
        Assert.StartsWith("http://telegram.test/bot123:abc/getUpdates", handler.Requests[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_stops_with_fatal_code_when_webhook_is_set()
    {
        var telegram = new FakeTelegram
        {
            Updates = new TelegramUpdatesResult(false, 409, Array.Empty<TelegramMessage>(), "Conflict"),
            Webhook = new TelegramWebhookInfo(true, "https://example.org/hook"),
        };

        int exitCode = await RunBotAsync(telegram, new FakeIntake());

        Assert.Equal(BotRunner.ExitFatal, exitCode);
        Assert.Contains(telegram.Logs, line => line.Contains("webhook", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(telegram.Sent);
    }

    [Fact]
    public async Task Bot_waits_and_retries_conflict_without_webhook()
    {
        var telegram = new FakeTelegram
        {
            Webhook = new TelegramWebhookInfo(true, null),
        };
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(false, 409, Array.Empty<TelegramMessage>(), "Conflict"));
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(
            true,
            200,
            new[] { new TelegramMessage(1, 42, "купить хлеб") }));

        var intake = new FakeIntake { Attempt = IntakeAttempt.Success(TaskResponse("купить хлеб")) };

        int exitCode = await RunBotAsync(telegram, intake);

        Assert.Equal(BotRunner.ExitOk, exitCode);
        Assert.Contains(TimeSpan.FromSeconds(5), telegram.Delays);
        Assert.Single(telegram.Sent);
        Assert.Contains("Задача: купить хлеб", telegram.Sent[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_truncates_text_before_sending_to_server()
    {
        string longText = new('я', BotRunner.MaxTextLength + 500);
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(1, 7, longText) }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        var intake = new FakeIntake { Attempt = IntakeAttempt.Success(TaskResponse("обрезано")) };

        await RunBotAsync(telegram, intake);

        Assert.Equal(BotRunner.MaxTextLength, intake.Received.Single().Length);
        Assert.Contains("длиннее", telegram.Sent.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_reports_failure_without_exception_text()
    {
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(1, 7, "купить хлеб") }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        var intake = new FakeIntake { Attempt = IntakeAttempt.Fail(503, IntakeFailure.ServerError, "System.Net.Http.HttpRequestException: boom") };

        await RunBotAsync(telegram, intake);

        string reply = telegram.Sent.Single().Text;
        Assert.Contains("ошибкой 503", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_answers_help_to_slash_commands_without_calling_the_server()
    {
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(1, 7, "/start") }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        var intake = new FakeIntake { Attempt = IntakeAttempt.Success(TaskResponse("не должно случиться")) };

        await RunBotAsync(telegram, intake);

        Assert.Empty(intake.Received);
        string reply = telegram.Sent.Single().Text;
        Assert.Contains("задачу", reply, StringComparison.Ordinal);
        Assert.Contains("станцию", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_lists_open_tasks_with_done_buttons()
    {
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(1, 7, "/tasks") }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        var intake = new FakeIntake
        {
            TaskList = new TaskListAttempt(true, 200, new[]
            {
                new OpenTaskDto(first, "купить хлеб", "new"),
                new OpenTaskDto(second, "позвонить маме", "in_progress"),
            }),
        };

        await RunBotAsync(telegram, intake);

        (long ChatId, string Text, InlineKeyboard? Keyboard) posted = telegram.Posted.Single();
        Assert.Equal(7, posted.ChatId);
        Assert.Contains("купить хлеб", posted.Text, StringComparison.Ordinal);
        Assert.Contains("(в работе)", posted.Text, StringComparison.Ordinal);
        Assert.NotNull(posted.Keyboard);
        Assert.Equal(2, posted.Keyboard!.Rows.Count);
        Assert.Equal("done:" + first.ToString("D"), posted.Keyboard.Rows[0][0].CallbackData);
        Assert.Equal("позвонить маме", posted.Keyboard.Rows[1][0].Text);
    }

    [Fact]
    public async Task Bot_says_when_there_are_no_open_tasks()
    {
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(1, 7, "/tasks") }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        await RunBotAsync(telegram, new FakeIntake());

        Assert.Contains("Открытых задач нет", telegram.Posted.Single().Text, StringComparison.Ordinal);
        Assert.Null(telegram.Posted.Single().Keyboard);
    }

    [Fact]
    public async Task Bot_closes_task_when_done_button_pressed()
    {
        Guid taskId = Guid.NewGuid();
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[]
        {
            new TelegramMessage(1, 7, string.Empty, "cb-1", "done:" + taskId.ToString("D")),
        }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        var intake = new FakeIntake();
        await RunBotAsync(telegram, intake);

        Assert.Equal(taskId, Assert.Single(intake.Completed));
        Assert.Contains("cb-1", telegram.Answered);
        Assert.Contains("закрыта", telegram.Posted.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_reports_task_that_cannot_be_closed()
    {
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[]
        {
            new TelegramMessage(1, 7, string.Empty, "cb-2", "done:" + Guid.NewGuid().ToString("D")),
        }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        var intake = new FakeIntake { CompleteResult = new TaskCommandAttempt(false, 400, IntakeFailure.BadRequest) };

        await RunBotAsync(telegram, intake);

        Assert.Contains("уже закрыта", telegram.Posted.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_passes_ask_intent_from_command()
    {
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(1, 7, "/ask сколько минут варить яйцо") }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        var intake = new FakeIntake { Attempt = IntakeAttempt.Success(TaskResponse("ответ")) };

        await RunBotAsync(telegram, intake);

        (string Text, string? Intent) request = Assert.Single(intake.Requests);
        Assert.Equal("сколько минут варить яйцо", request.Text);
        Assert.Equal("ask", request.Intent);
    }

    [Fact]
    public async Task Bot_offers_done_button_for_created_task()
    {
        var telegram = new FakeTelegram();
        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(1, 7, "купить хлеб") }));
        telegram.UpdatesQueue.Enqueue(TelegramUpdatesResult.Empty);

        IntakeResponseDto response = TaskResponse("купить хлеб");
        var intake = new FakeIntake { Attempt = IntakeAttempt.Success(response) };

        await RunBotAsync(telegram, intake);

        (long ChatId, string Text, InlineKeyboard? Keyboard) posted = telegram.Posted.Single();
        Assert.NotNull(posted.Keyboard);
        Assert.Equal("done:" + response.Task!.Id.ToString("D"), posted.Keyboard!.Rows[0][0].CallbackData);
    }

    [Fact]
    public void Callback_query_is_parsed_into_a_message()
    {
        const string json = """
            {"ok":true,"result":[
              {"update_id":5,"callback_query":{
                "id":"cb-9","data":"done:11111111-1111-1111-1111-111111111111",
                "message":{"message_id":3,"chat":{"id":42},"text":"Задачи"}}}
            ]}
            """;

        using JsonDocument document = JsonDocument.Parse(json);
        TelegramMessage message = Assert.Single(TelegramApiClient.ParseUpdates(document.RootElement));

        Assert.True(message.IsCallback);
        Assert.Equal("cb-9", message.CallbackQueryId);
        Assert.Equal("done:11111111-1111-1111-1111-111111111111", message.CallbackData);
        Assert.Equal(42, message.ChatId);
        Assert.Equal(5, message.UpdateId);
    }

    [Fact]
    public void Reply_markup_is_an_inline_keyboard()
    {
        InlineKeyboard keyboard = InlineKeyboard.Column(new InlineButton("Готово", "done:x"), new InlineButton("Ещё", "list"));
        JsonObject markup = TelegramApiClient.ToReplyMarkup(keyboard);

        JsonArray rows = markup["inline_keyboard"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Готово", rows[0]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("done:x", rows[0]!.AsArray()[0]!["callback_data"]!.GetValue<string>());
    }
    [Fact]
    public void Reply_failure_texts_are_human()
    {
        string unauthorized = IntakeReplyFormatter.FormatFailure(IntakeFailure.Unauthorized, 401);
        string unavailable = IntakeReplyFormatter.FormatFailure(IntakeFailure.ServerUnavailable, 0);
        string bad = IntakeReplyFormatter.FormatFailure(IntakeFailure.BadRequest, 413);

        Assert.Contains("токен", unauthorized, StringComparison.Ordinal);
        Assert.Contains("недоступен", unavailable, StringComparison.Ordinal);
        Assert.Contains("413", bad, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", unauthorized + unavailable + bad, StringComparison.Ordinal);
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
    public void Reply_returns_the_answer_to_a_question()
    {
        var response = new IntakeResponseDto(
            new IntakeTaskDto(Guid.NewGuid(), "сколько варить яйцо", "new", "telegram", null),
            new IntakeIntentDto("ask", 1d, Confident: true, "rules-v1+session"),
            Command: "ask",
            Executed: true,
            Error: null,
            Outcome: "executed",
            Result: System.Text.Json.Nodes.JsonNode.Parse(
                "{\"answer\":\"Варить 3–4 минуты.\",\"stub\":false,\"answerer\":\"openai:qwen3\"}"));

        string reply = IntakeReplyFormatter.Format(response);

        Assert.Equal("Варить 3–4 минуты.", reply);
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

[Fact]
    public async Task Transport_failure_is_retried_quickly_without_long_pause()
    {
        var telegram = new FakeTelegram { TransportFailuresBeforeSuccess = 2 };
        var intake = new FakeIntake();
        var options = new BotOptions { BotToken = "123:abc", PollTimeoutSeconds = 1 };
        var runner = new BotRunner(telegram, intake, options, telegram.Log, telegram.Delay);
        using var cts = new CancellationTokenSource();
        telegram.OnEmptyPoll = () => cts.Cancel();

        int exitCode = await runner.RunAsync(cts.Token);

        Assert.Equal(BotRunner.ExitOk, exitCode);
        Assert.Equal(2, telegram.Delays.Count(delay => delay == TimeSpan.FromMilliseconds(250)));
        Assert.All(telegram.Delays, delay => Assert.True(delay < TimeSpan.FromSeconds(1)));
        Assert.Contains(telegram.Logs, line => line.Contains("повтор 1/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Broken_channel_falls_back_to_short_backoff()
    {
        var telegram = new FakeTelegram { TransportFailuresBeforeSuccess = 100 };
        var intake = new FakeIntake();
        var options = new BotOptions { BotToken = "123:abc", PollTimeoutSeconds = 1 };
        using var cts = new CancellationTokenSource();
        var runner = new BotRunner(telegram, intake, options, telegram.Log, telegram.Delay);
        telegram.OnDelay = span =>
        {
            if (span >= TimeSpan.FromSeconds(3))
            {
                cts.Cancel();
            }
        };

        int exitCode = await runner.RunAsync(cts.Token);

        Assert.Equal(BotRunner.ExitOk, exitCode);
        Assert.Equal(TimeSpan.FromMilliseconds(250), telegram.Delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(250), telegram.Delays[1]);
        Assert.True(telegram.Delays[2] <= TimeSpan.FromSeconds(6), "backoff не должен превышать 6 секунд");
        Assert.Contains(telegram.Logs, line => line.Contains("сеть недоступна", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reminder_reaches_the_chat_even_without_a_new_message()
    {
        var telegram = new FakeTelegram
        {
            LastUpdate = new TelegramUpdatesResult(true, 200, new[] { new TelegramMessage(9, 777, "старое сообщение") }),
        };
        var intake = new FakeIntake();
        intake.DueReminders.Add(new DueReminderDto(Guid.NewGuid(), "выключить чайник", "сейчас", "pending"));

        await RunBotAsync(telegram, intake);

        (long chatId, string text, _) = Assert.Single(telegram.Posted);
        Assert.Equal(777, chatId);
        Assert.Contains("выключить чайник", text, StringComparison.Ordinal);
        Assert.Contains(telegram.Logs, line => line.Contains("напоминания пойдут в чат 777", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Due_reminder_is_sent_to_the_chat_that_wrote_last()
    {
        var telegram = new FakeTelegram();
        var intake = new FakeIntake();
        var reminderId = Guid.NewGuid();
        intake.DueReminders.Add(new DueReminderDto(reminderId, "полить цветы", "сегодня в 18:00", "sent"));

        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(
            true,
            200,
            new[] { new TelegramMessage(1, 555, "привет") }));

        await RunBotAsync(telegram, intake);

        (long chatId, string text, InlineKeyboard? keyboard) =
            Assert.Single(telegram.Posted, posted => posted.Text.Contains("полить цветы", StringComparison.Ordinal));

        Assert.Equal(555, chatId);
        Assert.Contains("⏰", text, StringComparison.Ordinal);
        Assert.Contains("сегодня в 18:00", text, StringComparison.Ordinal);
        Assert.NotNull(keyboard);

        InlineButton done = Assert.Single(keyboard!.Rows.SelectMany(row => row).Where(button => button.Text == "Готово"));
        Assert.Equal("rem:ack:" + reminderId.ToString("D"), done.CallbackData);
    }

    [Fact]
    public async Task Reminder_can_be_acknowledged_by_button()
    {
        var telegram = new FakeTelegram();
        var intake = new FakeIntake();
        var reminderId = Guid.NewGuid();

        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(
            true,
            200,
            new[] { new TelegramMessage(2, 555, string.Empty, "cb-1", "rem:ack:" + reminderId.ToString("D")) }));

        await RunBotAsync(telegram, intake);

        Assert.Equal(reminderId, Assert.Single(intake.Acknowledged));
        Assert.Contains(telegram.AnsweredWithText, answer => answer.Text == "Отмечено");
    }

    [Fact]
    public async Task Reminder_can_be_postponed_by_button()
    {
        var telegram = new FakeTelegram();
        var intake = new FakeIntake();
        var reminderId = Guid.NewGuid();

        telegram.UpdatesQueue.Enqueue(new TelegramUpdatesResult(
            true,
            200,
            new[] { new TelegramMessage(3, 555, string.Empty, "cb-2", "rem:snooze:" + reminderId.ToString("D")) }));

        await RunBotAsync(telegram, intake);

        (Guid id, int minutes) = Assert.Single(intake.Snoozed);
        Assert.Equal(reminderId, id);
        Assert.Equal(10, minutes);
        Assert.Contains(telegram.Posted, posted => posted.Text.Contains("через 10 минут", StringComparison.Ordinal));
    }

    private static async Task<int> RunBotAsync(FakeTelegram telegram, FakeIntake intake)
    {
        var options = new BotOptions { BotToken = "123:abc", PollTimeoutSeconds = 1 };
        var runner = new BotRunner(telegram, intake, options, telegram.Log, telegram.Delay);
        using var cts = new CancellationTokenSource();

        // Пустая очередь = «новых сообщений нет»; после третьего пустого ответа останавливаем.
        telegram.OnEmptyPoll = () =>
        {
            if (telegram.EmptyPolls >= 2)
            {
                cts.Cancel();
            }
        };

        try
        {
            return await runner.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return BotRunner.ExitOk;
        }
    }

    private static IntakeResponseDto TaskResponse(string title)
        => new(
            new IntakeTaskDto(Guid.NewGuid(), title, "new", "telegram", title),
            new IntakeIntentDto("add_task", 1d, Confident: true, "rules-v1"),
            Command: "add_task",
            Executed: false,
            Error: null,
            Outcome: "created",
            Result: null);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeTelegram : ITelegramApi
    {
        public Queue<TelegramUpdatesResult> UpdatesQueue { get; } = new();

        public TelegramUpdatesResult Updates { get; set; } = TelegramUpdatesResult.Empty;

        public TelegramWebhookInfo Webhook { get; set; } = new(true, null);

        public List<string> Logs { get; } = new();

        public List<(long ChatId, string Text)> Sent { get; } = new();

        public List<TimeSpan> Delays { get; } = new();

        public int EmptyPolls { get; private set; }

        public Action? OnEmptyPoll { get; set; }

        public int TransportFailuresBeforeSuccess { get; set; }

        public Action<TimeSpan>? OnDelay { get; set; }

        public TelegramUpdatesResult LastUpdate { get; set; } = TelegramUpdatesResult.Empty;

        public Task<TelegramUpdatesResult> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken = default)
        {
            // offset = -1 — «последнее обновление, не сдвигая очередь»: бот так узнаёт чат после перезапуска.
            if (offset < 0)
            {
                return Task.FromResult(LastUpdate);
            }

            if (TransportFailuresBeforeSuccess > 0)
            {
                TransportFailuresBeforeSuccess--;
                throw new HttpRequestException("Соединение закрыто удалённым хостом");
            }

if (UpdatesQueue.Count > 0)
            {
                return Task.FromResult(UpdatesQueue.Dequeue());
            }

            EmptyPolls++;
            OnEmptyPoll?.Invoke();
            return Task.FromResult(Updates);
        }

        public List<(long ChatId, string Text, InlineKeyboard? Keyboard)> Posted { get; } = new();

        public List<string> Answered { get; } = new();

        public List<(string CallbackId, string? Text)> AnsweredWithText { get; } = new();

        public Task<TelegramSendResult> SendMessageAsync(
            long chatId,
            string text,
            InlineKeyboard? keyboard = null,
            CancellationToken cancellationToken = default)
        {
            Sent.Add((chatId, text));
            Posted.Add((chatId, text, keyboard));
            return Task.FromResult(new TelegramSendResult(true, 200));
        }

        public Task<TelegramSendResult> AnswerCallbackAsync(
            string callbackQueryId,
            string? text = null,
            CancellationToken cancellationToken = default)
        {
            Answered.Add(callbackQueryId);
            AnsweredWithText.Add((callbackQueryId, text));
            return Task.FromResult(new TelegramSendResult(true, 200));
        }

        public Task<TelegramWebhookInfo> GetWebhookInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Webhook);

        public void Log(string line) => Logs.Add(line);

        public Task Delay(TimeSpan span, CancellationToken cancellationToken)
        {
            Delays.Add(span);
            OnDelay?.Invoke(span);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIntake : IKoukiIntake
    {
        public IntakeAttempt Attempt { get; set; } = IntakeAttempt.Fail(0, IntakeFailure.ServerUnavailable);

        public List<string> Received { get; } = new();

        public List<(string Text, string? Intent)> Requests { get; } = new();

        public TaskListAttempt TaskList { get; set; } = new(true, 200, Array.Empty<OpenTaskDto>());

        public List<Guid> Completed { get; } = new();

        public TaskCommandAttempt CompleteResult { get; set; } = new(true, 200);

        /// <summary>Напоминания, которые «созрели»: после выдачи список очищается, как на сервере.</summary>
        public List<DueReminderDto> DueReminders { get; } = new();

        public List<Guid> Acknowledged { get; } = new();

        public List<(Guid Id, int Minutes)> Snoozed { get; } = new();

        public Task<DueReminderAttempt> TakeDueRemindersAsync(int limit = 5, CancellationToken cancellationToken = default)
        {
            DueReminderDto[] due = DueReminders.Take(limit).ToArray();
            DueReminders.Clear();
            return Task.FromResult(new DueReminderAttempt(true, 200, due));
        }

        public Task<TaskCommandAttempt> AcknowledgeReminderAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Acknowledged.Add(id);
            return Task.FromResult(new TaskCommandAttempt(true, 200));
        }

        public Task<TaskCommandAttempt> SnoozeReminderAsync(Guid id, int minutes, CancellationToken cancellationToken = default)
        {
            Snoozed.Add((id, minutes));
            return Task.FromResult(new TaskCommandAttempt(true, 200));
        }

        public Task<IntakeAttempt> SubmitAsync(string text, CancellationToken cancellationToken = default)
            => SubmitAsync(text, intent: null, cancellationToken);

        public Task<IntakeAttempt> SubmitAsync(string text, string? intent, CancellationToken cancellationToken = default)
        {
            Received.Add(text);
            Requests.Add((text, intent));
            return Task.FromResult(Attempt);
        }

        public Task<TaskListAttempt> ListOpenTasksAsync(int limit = 5, CancellationToken cancellationToken = default)
            => Task.FromResult(TaskList);

        public Task<TaskCommandAttempt> CompleteTaskAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Completed.Add(id);
            return Task.FromResult(CompleteResult);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public List<Uri> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests.Add(request.RequestUri!);
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return _responder(request);
        }
    }
}
