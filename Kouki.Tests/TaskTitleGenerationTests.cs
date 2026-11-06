using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using System.Text.Json.Nodes;
using RealAddTaskHandler = Kouki.Kagedama.Commands.AddTaskHandler;
using RealAddTaskCommand = Kouki.Kagedama.Commands.AddTaskCommand;

namespace Kouki.Tests;

public sealed class TaskTitleGenerationTests : IDisposable
{
    private readonly string _databasePath;

    public TaskTitleGenerationTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"kouki-title-test-{Guid.NewGuid():N}.sqlite");
        DatabaseInitializer.Initialize(_databasePath);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("запиши мне пожалуйста купить молоко в магазине", "Купить молоко в магазине")]
    [InlineData("Запиши, при записи карточек надо убирать мусор", "При записи карточек надо убирать мусор")]
    [InlineData("надо починить кран на кухне", "Починить кран на кухне")]
    [InlineData("добавь задачу сделать отчёт", "Сделать отчёт")]
    [InlineData("сохрани таск поправить баг в коде", "Поправить баг в коде")]
    [InlineData("пожалуйста запиши полить цветы", "Полить цветы")]
    [InlineData("купить хлеб", "купить хлеб")]
    public async Task Rule_title_generator_strips_instruct_prefixes(string raw, string expected)
    {
        var generator = RuleTaskTitleGenerator.Instance;
        string result = await generator.GenerateTitleAsync(raw);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AddTask_stores_clean_title_and_preserves_raw_text()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new RealAddTaskHandler(store, RuleTaskTitleGenerator.Instance);

        string rawPhrase = "запиши мне пожалуйста купить молоко в магазине";
        JsonNode result = await handler.HandleAsync(new RealAddTaskCommand(rawPhrase, TaskSources.Station));

        Assert.True(result["created"]?.GetValue<bool>());
        Assert.Equal("Записала: Купить молоко в магазине", result["answer"]?.GetValue<string>());

        Guid taskId = Guid.Parse(result["taskId"]!.GetValue<string>());
        TaskRecord? stored = await store.GetAsync(taskId);

        Assert.NotNull(stored);
        Assert.Equal("Купить молоко в магазине", stored.Title);
        Assert.Equal(rawPhrase, stored.RawText);
    }
}
