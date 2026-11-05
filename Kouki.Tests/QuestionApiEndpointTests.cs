using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kouki.Tests;

public sealed class QuestionApiEndpointTests : IClassFixture<ApiHostFixture>
{
    private readonly ApiHostFixture _host;

    public QuestionApiEndpointTests(ApiHostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task Get_questions_returns_empty_or_items()
    {
        var response = await _host.Client.GetAsync("/api/questions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<QuestionRecord>>();
        Assert.NotNull(list);
    }

    [Fact]
    public async Task Post_question_to_task_creates_task_and_links()
    {
        IQuestionStore store = _host.Services.GetRequiredService<IQuestionStore>();
        var record = new QuestionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            "test",
            "Купить билеты в кино?",
            "Билеты на вечерний сеанс стоят 400р.");
        await store.AddAsync(record);

        // Создаем задачу из вопроса через API
        var response = await _host.Client.PostAsync($"/api/questions/{record.Id}/task", null);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);
        Assert.True(payload.ContainsKey("id") || payload.ContainsKey("taskId"));

        // Проверяем, что в вопросе проставился task_id
        QuestionRecord? updated = await store.GetAsync(record.Id);
        Assert.NotNull(updated);
        Assert.NotNull(updated.TaskId);
    }

    [Fact]
    public async Task Get_actions_with_query_filters_results()
    {
        IActionLog log = _host.Services.GetRequiredService<IActionLog>();
        await log.RecordAsync(new ActionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            "api",
            "command",
            "custom_action",
            "special_marker_777",
            "executed"));

        var response = await _host.Client.GetAsync("/api/actions?q=special_marker_777");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var array = await response.Content.ReadFromJsonAsync<JsonArray>();
        Assert.NotNull(array);
        Assert.Single(array);
    }
}
