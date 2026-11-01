using System.Text.Json.Nodes;
using Kouki.Kagedama;
using Kouki.Kagedama.Bot;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Mcp;
using Kouki.Kagedama.Transport;
using Kouki.Shimi.Context;
using Kouki.Yasabi;
using Microsoft.Extensions.DependencyInjection;
using Router = Kouki.Mugura.Dispatcher.IRouter;

namespace Kouki.Tests;

/// <summary>
/// Теги наружу: ответы задач в API, список с фильтром, бот, MCP и разбор ответа модели.
/// </summary>
public sealed class TagExposureTests
{
    [Fact]
    public void Task_dto_always_has_tags_array()
    {
        TaskRecord task = Kouki.Kagedama.TaskFactory.Create("купить хлеб", TaskSources.Api);

        JsonObject empty = Assert.IsType<JsonObject>(ApiMapping.ToJson(ApiMapping.ToDto(task)));
        JsonArray emptyTags = Assert.IsType<JsonArray>(empty["tags"]);
        Assert.Empty(emptyTags);

        JsonObject tagged = Assert.IsType<JsonObject>(
            ApiMapping.ToJson(ApiMapping.ToDto(task, new[] { "работа", "срочно" })));
        JsonArray tags = Assert.IsType<JsonArray>(tagged["tags"]);

        Assert.Equal(new[] { "работа", "срочно" }, tags.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Get_task_returns_tags_even_without_tag_store()
    {
        using var fixture = new TagIntakeFixture();
        TaskRecord task = await fixture.NewTaskAsync("купить хлеб", "магазин");

        JsonObject payload = Assert.IsType<JsonObject>(
            await new GetTaskHandler(fixture.Store, fixture.Tags).HandleAsync(new GetTaskCommand(task.Id.ToString())));

        JsonArray tags = Assert.IsType<JsonArray>(payload["tags"]);
        Assert.Equal(new[] { "магазин" }, tags.Select(node => node!.GetValue<string>()));

        // Хост читает тот же объект через DTO: теги должны пережить сериализацию.
        TaskDto? dto = ApiMapping.FromTaskJson(payload);
        Assert.NotNull(dto);
        Assert.Equal(new[] { "магазин" }, dto!.Tags);

        // Без хранилища тегов задача всё равно отдаётся — с пустым массивом, а не null.
        JsonObject bare = Assert.IsType<JsonObject>(
            await new GetTaskHandler(fixture.Store).HandleAsync(new GetTaskCommand(task.Id.ToString())));
        Assert.Empty(Assert.IsType<JsonArray>(bare["tags"]));
    }

    [Fact]
    public async Task List_tasks_returns_tags_and_filters_by_tag()
    {
        using var fixture = new TagIntakeFixture();
        TaskRecord tagged = await fixture.NewTaskAsync("купить хлеб", "магазин");
        TaskRecord plain = await fixture.NewTaskAsync("позвонить маме");

        var handler = new ListTasksHandler(fixture.Store, fixture.Tags);

        JsonObject all = Assert.IsType<JsonObject>(await handler.HandleAsync(new ListTasksCommand(null, 10)));
        JsonArray tasks = Assert.IsType<JsonArray>(all["tasks"]);
        Assert.Equal(2, tasks.Count);

        foreach (JsonObject item in tasks.OfType<JsonObject>())
        {
            Assert.IsType<JsonArray>(item["tags"]);
        }

        // Фильтр нормализует тег: «#Магазин» и «магазин» — одно и то же.
        JsonObject filtered = Assert.IsType<JsonObject>(
            await handler.HandleAsync(new ListTasksCommand(null, 10, "#Магазин")));
        JsonObject only = Assert.Single(Assert.IsType<JsonArray>(filtered["tasks"]).OfType<JsonObject>());

        Assert.Equal(tagged.Id, Guid.Parse(only["id"]!.GetValue<string>()));
        Assert.NotEqual(plain.Id, tagged.Id);

        // Пустой после нормализации тег — это ошибка, а не «все задачи».
        JsonObject broken = Assert.IsType<JsonObject>(
            await handler.HandleAsync(new ListTasksCommand(null, 10, "#")));
        Assert.Equal(ListTasksHandler.EmptyTagError, broken["error"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(broken["tasks"]));
    }

    [Fact]
    public async Task Mcp_list_tasks_accepts_tag()
    {
        using var fixture = new TagIntakeFixture(options: McpOptions());
        TaskRecord tagged = await fixture.NewTaskAsync("купить хлеб", "магазин");
        await fixture.NewTaskAsync("позвонить маме");

        var tools = new KoukiMcpTools(
            fixture.Intake,
            fixture.Provider.GetRequiredService<Router>(),
            fixture.Provider.GetRequiredService<IContextRegistry>());

        JsonObject payload = Assert.IsType<JsonObject>(
            JsonNode.Parse(await tools.InvokeAsync("kouki_list_tasks", new JsonObject { ["tag"] = "магазин" })));

        Assert.Equal(1, payload["count"]!.GetValue<int>());
        JsonObject item = Assert.Single(Assert.IsType<JsonArray>(payload["tasks"]).OfType<JsonObject>());
        Assert.Equal(tagged.Id, Guid.Parse(item["id"]!.GetValue<string>()));
        Assert.Equal("магазин", Assert.IsType<JsonArray>(item["tags"])[0]!.GetValue<string>());
    }

    [Fact]
    public async Task Mcp_rejects_phrase_of_tags_only()
    {
        using var fixture = new TagIntakeFixture(options: McpOptions());
        var tools = new KoukiMcpTools(
            fixture.Intake,
            fixture.Provider.GetRequiredService<Router>(),
            fixture.Provider.GetRequiredService<IContextRegistry>());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => tools.InvokeAsync("kouki_add_task", new JsonObject { ["text"] = "#работа #дом" }));

        Assert.Contains(IntakeRequestPolicy.TagsOnlyMessage, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync("tasks"));
    }

    private static IntakeOptions McpOptions() => new()
    {
        ConfidenceThreshold = 0.75,
        DefaultSource = TaskSources.Mcp,
    };

    [Fact]
    public void Bot_reply_mentions_tags_when_they_exist()
    {
        var tagged = new IntakeTaskDto(
            Guid.NewGuid(),
            "Купить хлеб",
            TaskStatuses.New,
            TaskSources.Api,
            "Купить хлеб")
        {
            Tags = new[] { "работа", "срочно" },
        };

        string text = IntakeReplyFormatter.Format(new IntakeResponseDto(
            tagged,
            new IntakeIntentDto("add_task", 0.9, true, "rules"),
            Command: null,
            Executed: true,
            Error: null));

        Assert.Contains("Задача: Купить хлеб", text, StringComparison.Ordinal);
        Assert.Contains("теги: работа, срочно", text, StringComparison.Ordinal);

        var plain = new IntakeTaskDto(Guid.NewGuid(), "Позвонить маме", TaskStatuses.New, TaskSources.Api, null);
        string withoutTags = IntakeReplyFormatter.Format(new IntakeResponseDto(
            plain,
            new IntakeIntentDto("add_task", 0.9, true, "rules"),
            Command: null,
            Executed: true,
            Error: null));

        Assert.DoesNotContain("теги:", withoutTags, StringComparison.Ordinal);
    }

    [Fact]
    public void Bot_list_mentions_tags_of_each_task()
    {
        JsonArray tasks = new()
        {
            new JsonObject
            {
                ["title"] = "Купить хлеб",
                ["status"] = TaskStatuses.New,
                ["tags"] = new JsonArray("работа"),
            },
        };

        string text = IntakeReplyFormatter.Format(new IntakeResponseDto(
            Task: null,
            Intent: null,
            Command: "list_tasks",
            Executed: true,
            Error: null,
            Result: new JsonObject { ["tasks"] = tasks }));

        Assert.Contains("теги: работа", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_tags_are_read_as_they_are()
    {
        string body = """
        {"choices":[{"message":{"content":"{\"intent\": \"add_task\", \"confidence\": 0.9, \"tags\": [\"Работа\", \"\", 42, \"магазин\"]}"}}]}
        """;

        IntentResult intent = LlmIntentClassifier.Parse(body, LlmIntentClassifier.DefaultIntents);

        // Классификатор не нормализует: это дело приёма (он один знает про лимиты task_tags).
        Assert.Equal(new[] { "Работа", "магазин" }, intent.Tags);
    }

    [Fact]
    public void Broken_tags_field_does_not_break_classification()
    {
        string body = """
        {"choices":[{"message":{"content":"{\"intent\": \"add_task\", \"confidence\": 0.9, \"tags\": \"работа\"}"}}]}
        """;

        IntentResult intent = LlmIntentClassifier.Parse(body, LlmIntentClassifier.DefaultIntents);

        Assert.Equal("add_task", intent.Intent);
        Assert.Null(intent.Tags);
    }

    [Fact]
    public void Prompt_asks_for_tags_and_has_room_for_them()
    {
        Assert.Equal(128, new LlmIntentOptions().MaxOutputTokens);
        Assert.Contains(
            "тег",
            LlmIntentClassifier.BuildSystemPrompt(LlmIntentClassifier.DefaultIntents),
            StringComparison.OrdinalIgnoreCase);
    }
}
