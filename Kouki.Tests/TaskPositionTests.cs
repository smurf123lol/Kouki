using Microsoft.Data.Sqlite;

using Kouki.Kagedama;

using TaskFactory = Kouki.Kagedama.TaskFactory;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;

namespace Kouki.Tests;

/// <summary>
/// Порядок задач: <c>position ASC, created_at DESC, id ASC</c>, выдача
/// следующей позиции в паре «статус + родитель» и сохранение позиции при изменении.
/// </summary>
public sealed class TaskPositionTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    /// <summary>Готовит временный каталог под базу.</summary>
    public TaskPositionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-position-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "kouki.sqlite");
        DatabaseInitializer.Initialize(_databasePath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог мог остаться занятым — тест от этого не зависит.
        }
    }

    [Fact]
    public async Task Tasks_are_ordered_by_position_then_created_at_desc_then_id()
    {
        var store = new SqliteTaskStore(_databasePath);
        DateTimeOffset old = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset tied = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset latest = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        TaskRecord older = TaskFactory.Create("старая", TaskSources.Api, now: old);
        await store.AddAsync(older);
        TaskRecord tieA = TaskFactory.Create("ровня A", TaskSources.Api, now: tied);
        await store.AddAsync(tieA);
        TaskRecord tieB = TaskFactory.Create("ровня B", TaskSources.Api, now: tied);
        await store.AddAsync(tieB);
        TaskRecord newest = TaskFactory.Create("новая", TaskSources.Api, now: latest);
        await store.AddAsync(newest);

        SetPosition(older.Id, 0);
        SetPosition(tieA.Id, 0);
        SetPosition(tieB.Id, 0);
        SetPosition(newest.Id, 1);

        IReadOnlyList<TaskRecord> tasks = await store.ListAsync(null, 100);

        string firstTied = string.CompareOrdinal(tieA.Id.ToString(), tieB.Id.ToString()) < 0
            ? tieA.Id.ToString()
            : tieB.Id.ToString();
        string secondTied = firstTied == tieA.Id.ToString() ? tieB.Id.ToString() : tieA.Id.ToString();
        string expected = string.Join(",", firstTied, secondTied, older.Id, newest.Id);

        Assert.Equal(expected, string.Join(",", tasks.Select(task => task.Id)));
    }

    [Fact]
    public async Task New_task_gets_next_position_in_its_status_and_parent_pair()
    {
        var store = new SqliteTaskStore(_databasePath);

        TaskRecord firstRoot = TaskFactory.Create("корень 1", TaskSources.Api);
        await store.AddAsync(firstRoot);
        Assert.Equal(TaskPositions.First, firstRoot.Position);

        TaskRecord secondRoot = TaskFactory.Create("корень 2", TaskSources.Api);
        await store.AddAsync(secondRoot);
        Assert.Equal(1, secondRoot.Position);

        TaskRecord doneRoot = TaskFactory.Create("готовый корень", TaskSources.Api, TaskStatuses.Done);
        await store.AddAsync(doneRoot);
        Assert.Equal(TaskPositions.First, doneRoot.Position);

        TaskRecord secondDoneRoot = TaskFactory.Create("готовый корень 2", TaskSources.Api, TaskStatuses.Done);
        await store.AddAsync(secondDoneRoot);
        Assert.Equal(1, secondDoneRoot.Position);

        TaskRecord firstChild = TaskFactory.Create("шаг 1", TaskSources.Api);
        firstChild.ParentId = firstRoot.Id;
        await store.AddAsync(firstChild);
        Assert.Equal(TaskPositions.First, firstChild.Position);

        TaskRecord secondChild = TaskFactory.Create("шаг 2", TaskSources.Api);
        secondChild.ParentId = firstRoot.Id;
        await store.AddAsync(secondChild);
        Assert.Equal(1, secondChild.Position);
    }

    [Fact]
    public async Task Update_does_not_reset_position_parent_or_tree()
    {
        var store = new SqliteTaskStore(_databasePath);

        TaskRecord root = TaskFactory.Create("корень", TaskSources.Api);
        await store.AddAsync(root);
        TaskRecord firstChild = TaskFactory.Create("шаг 1", TaskSources.Api);
        firstChild.ParentId = root.Id;
        await store.AddAsync(firstChild);
        TaskRecord secondChild = TaskFactory.Create("шаг 2", TaskSources.Api);
        secondChild.ParentId = root.Id;
        await store.AddAsync(secondChild);

        bool updated = await store.UpdateAsync(
            firstChild.Id,
            TaskStatuses.Done,
            "шаг 1 после правки",
            TaskPriorities.High);

        Assert.True(updated);
        TaskRecord stored = (await store.GetAsync(firstChild.Id))!;
        Assert.Equal(TaskStatuses.Done, stored.Status);
        Assert.Equal("шаг 1 после правки", stored.Title);
        Assert.Equal(TaskPriorities.High, stored.Priority);
        Assert.Equal(root.Id, stored.ParentId);
        Assert.Equal(root.Id, stored.TreeId);
        Assert.Equal(TaskPositions.First, stored.Position);

        TaskRecord nextDoneChild = TaskFactory.Create("шаг 3", TaskSources.Api, TaskStatuses.Done);
        nextDoneChild.ParentId = root.Id;
        await store.AddAsync(nextDoneChild);
        Assert.Equal(1, nextDoneChild.Position);
    }

    /// <summary>Переставляет задачу напрямую, чтобы проверить порядок независимо от добавления.</summary>
    private void SetPosition(Guid id, int position)
    {
        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET position = $position WHERE id = $id";
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }
}
