using System.Text.Json.Nodes;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kouki.Tests;

public sealed class QuestionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteSession _session;
    private readonly SqliteQuestionStore _store;

    public QuestionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kouki-qtests-" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "test.sqlite");
        DatabaseInitializer.Initialize(_dbPath);
        _session = new SqliteSession(_dbPath);
        _store = new SqliteQuestionStore(_session);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Add_and_get_question()
    {
        var record = new QuestionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            "station",
            "Сколько варить яйцо?",
            "Варить 8-10 минут.",
            Model: "qwen3",
            DurationMs: 420);

        Guid id = await _store.AddAsync(record);
        Assert.Equal(record.Id, id);

        QuestionRecord? loaded = await _store.GetAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal(record.Question, loaded.Question);
        Assert.Equal(record.Answer, loaded.Answer);
        Assert.Equal("station", loaded.Source);
        Assert.Equal("qwen3", loaded.Model);
        Assert.Equal(420, loaded.DurationMs);
        Assert.Null(loaded.TaskId);
    }

    [Fact]
    public async Task List_with_pagination_and_query()
    {
        for (int i = 1; i <= 5; i++)
        {
            await _store.AddAsync(new QuestionRecord(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"),
                "voice",
                $"Вопрос {i}: рецепт борща",
                $"Ответ {i}: варить мясо"));
        }

        await _store.AddAsync(new QuestionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
            "voice",
            "Какая погода завтра?",
            "Завтра солнечно"));

        Assert.Equal(6, await _store.CountAsync());

        // Пагинация
        IReadOnlyList<QuestionRecord> page1 = await _store.ListAsync(2, 0);
        Assert.Equal(2, page1.Count);

        // Поиск по слову
        IReadOnlyList<QuestionRecord> found = await _store.ListAsync(10, 0, "погода");
        Assert.Single(found);
        Assert.Equal("Какая погода завтра?", found[0].Question);

        IReadOnlyList<QuestionRecord> foundAnswer = await _store.ListAsync(10, 0, "мясо");
        Assert.Equal(5, foundAnswer.Count);
    }

    [Fact]
    public async Task Link_task_updates_task_id()
    {
        var taskStore = new SqliteTaskStore(_session);
        var task = Kouki.Kagedama.TaskFactory.Create("Починить кран", "test");
        await taskStore.AddAsync(task);

        var record = new QuestionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            "station",
            "Как починить кран?",
            "Перекрыть воду и заменить прокладку.");

        await _store.AddAsync(record);

        bool linked = await _store.LinkTaskAsync(record.Id, task.Id);
        Assert.True(linked);

        QuestionRecord? loaded = await _store.GetAsync(record.Id);
        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded.TaskId);
    }
}
