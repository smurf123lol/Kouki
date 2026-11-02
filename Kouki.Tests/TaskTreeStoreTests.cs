using Microsoft.Data.Sqlite;

using Kouki.Kagedama;

using TaskFactory = Kouki.Kagedama.TaskFactory;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;

namespace Kouki.Tests;

/// <summary>
/// Дерево задач в хранилище: миграция старой базы, наследование <c>tree_id</c>,
/// запрет родителя-себя и фильтры <c>topLevel</c>/<c>parentId</c>.
/// </summary>
public sealed class TaskTreeStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    /// <summary>Готовит временный каталог под базу.</summary>
    public TaskTreeStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "kouki.sqlite");
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
    public async Task Legacy_database_is_migrated_and_second_initialize_changes_nothing()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        CreateLegacyDatabase(
            (first, "2026-01-02T03:04:05.0000000+00:00"),
            (second, "2026-01-03T04:05:06.0000000+00:00"));

        DatabaseInitializer.Initialize(_databasePath);

        var store = new SqliteTaskStore(_databasePath);
        TaskRecord firstTask = (await store.GetAsync(first))!;
        TaskRecord secondTask = (await store.GetAsync(second))!;

        foreach (TaskRecord task in new[] { firstTask, secondTask })
        {
            Assert.Equal(task.Id, task.TreeId);
            Assert.Null(task.ParentId);
            Assert.Equal(TaskPositions.First, task.Position);
        }

        List<string> treesAfterFirstRun = ReadTrees(_databasePath);
        Assert.Equal(2, treesAfterFirstRun.Count);

        DatabaseInitializer.Initialize(_databasePath);

        Assert.Equal(string.Join(",", treesAfterFirstRun), string.Join(",", ReadTrees(_databasePath)));
        TaskRecord firstAgain = (await store.GetAsync(first))!;
        Assert.Equal(first, firstAgain.TreeId);
        Assert.Null(firstAgain.ParentId);
        Assert.Equal(TaskPositions.First, firstAgain.Position);
    }

    [Fact]
    public async Task Empty_database_gets_tree_schema()
    {
        DatabaseInitializer.Initialize(_databasePath);

        var store = new SqliteTaskStore(_databasePath);

        Assert.Empty(await store.ListAsync(null, 100));
        Assert.Empty(ReadTrees(_databasePath));
    }

    [Fact]
    public async Task Subtask_inherits_parent_tree_and_position_is_counted_per_parent()
    {
        DatabaseInitializer.Initialize(_databasePath);
        var store = new SqliteTaskStore(_databasePath);

        TaskRecord root = TaskFactory.Create("корень", TaskSources.Api);
        await store.AddAsync(root);
        Assert.Equal(root.Id, root.TreeId);
        Assert.Equal(TaskPositions.First, root.Position);

        TaskRecord child = TaskFactory.Create("шаг", TaskSources.Api);
        child.ParentId = root.Id;
        await store.AddAsync(child);
        Assert.Equal(root.Id, child.TreeId);
        Assert.Equal(root.Id, child.ParentId);
        Assert.Equal(TaskPositions.First, child.Position);

        TaskRecord nextChild = TaskFactory.Create("ещё шаг", TaskSources.Api);
        nextChild.ParentId = root.Id;
        await store.AddAsync(nextChild);
        Assert.Equal(1, nextChild.Position);

        TaskRecord stored = (await store.GetAsync(child.Id))!;
        Assert.Equal(root.Id, stored.TreeId);
        Assert.Equal(root.Id, stored.ParentId);
        Assert.Equal(TaskPositions.First, stored.Position);
        Assert.Single(ReadTrees(_databasePath));
    }

    [Fact]
    public async Task Task_cannot_be_its_own_parent()
    {
        DatabaseInitializer.Initialize(_databasePath);
        var store = new SqliteTaskStore(_databasePath);

        TaskRecord task = TaskFactory.Create("сама себе родитель", TaskSources.Api);
        task.ParentId = task.Id;

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(task));

        Assert.Contains("родителем самой себе", error.Message);
        Assert.Empty(await store.ListAsync(null, 100));
    }

    [Fact]
    public async Task Top_level_and_parent_filters_return_the_right_tasks()
    {
        DatabaseInitializer.Initialize(_databasePath);
        var store = new SqliteTaskStore(_databasePath);

        TaskRecord firstRoot = TaskFactory.Create("первый корень", TaskSources.Api);
        await store.AddAsync(firstRoot);
        TaskRecord secondRoot = TaskFactory.Create("второй корень", TaskSources.Api);
        await store.AddAsync(secondRoot);

        TaskRecord firstChild = TaskFactory.Create("первый шаг", TaskSources.Api);
        firstChild.ParentId = firstRoot.Id;
        await store.AddAsync(firstChild);
        TaskRecord secondChild = TaskFactory.Create("второй шаг", TaskSources.Api);
        secondChild.ParentId = firstRoot.Id;
        await store.AddAsync(secondChild);

        TaskRecord grandchild = TaskFactory.Create("шаг шага", TaskSources.Api);
        grandchild.ParentId = firstChild.Id;
        await store.AddAsync(grandchild);

        IReadOnlyList<TaskRecord> topLevel = await store.ListAsync(null, 100, topLevel: true);
        Assert.Equal(
            string.Join(",", new[] { firstRoot.Id, secondRoot.Id }.OrderBy(id => id.ToString(), StringComparer.Ordinal)),
            string.Join(",", topLevel.Select(task => task.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal)));

        IReadOnlyList<TaskRecord> children = await store.ListAsync(null, 100, parentId: firstRoot.Id);
        Assert.Equal(
            string.Join(",", new[] { firstChild.Id, secondChild.Id }),
            string.Join(",", children.Select(task => task.Id)));

        Assert.Equal(5, (await store.ListAsync(null, 100)).Count);
    }

    [Fact]
    public async Task Cascade_delete_removes_subtree_and_empty_tree()
    {
        DatabaseInitializer.Initialize(_databasePath);
        var store = new SqliteTaskStore(_databasePath);

        TaskRecord root = TaskFactory.Create("корень", TaskSources.Api);
        await store.AddAsync(root);
        TaskRecord child = TaskFactory.Create("шаг", TaskSources.Api);
        child.ParentId = root.Id;
        await store.AddAsync(child);
        TaskRecord grandchild = TaskFactory.Create("шаг шага", TaskSources.Api);
        grandchild.ParentId = child.Id;
        await store.AddAsync(grandchild);
        TaskRecord other = TaskFactory.Create("другое дерево", TaskSources.Api);
        await store.AddAsync(other);

        TaskDeleteResult result = await store.DeleteAsync(root.Id);

        Assert.True(result.Deleted);
        Assert.Equal(2, result.RemovedSubtasks);
        Assert.Null(await store.GetAsync(root.Id));
        Assert.Null(await store.GetAsync(child.Id));
        Assert.Null(await store.GetAsync(grandchild.Id));
        Assert.NotNull(await store.GetAsync(other.Id));
        Assert.Equal(other.Id.ToString(), string.Join(",", ReadTrees(_databasePath)));
    }

    [Fact]
    public async Task Detach_delete_keeps_children_in_the_same_tree()
    {
        DatabaseInitializer.Initialize(_databasePath);
        var store = new SqliteTaskStore(_databasePath);

        TaskRecord root = TaskFactory.Create("корень", TaskSources.Api);
        await store.AddAsync(root);
        TaskRecord firstChild = TaskFactory.Create("шаг 1", TaskSources.Api);
        firstChild.ParentId = root.Id;
        await store.AddAsync(firstChild);
        TaskRecord secondChild = TaskFactory.Create("шаг 2", TaskSources.Api);
        secondChild.ParentId = root.Id;
        await store.AddAsync(secondChild);

        TaskDeleteResult result = await store.DeleteAsync(root.Id, TaskDeleteMode.Detach);

        Assert.True(result.Deleted);
        Assert.Equal(2, result.RemovedSubtasks);
        Assert.Null(await store.GetAsync(root.Id));

        TaskRecord storedFirst = (await store.GetAsync(firstChild.Id))!;
        TaskRecord storedSecond = (await store.GetAsync(secondChild.Id))!;
        Assert.Null(storedFirst.ParentId);
        Assert.Null(storedSecond.ParentId);
        Assert.Equal(root.Id, storedFirst.TreeId);
        Assert.Equal(root.Id, storedSecond.TreeId);
        Assert.Equal(root.Id.ToString(), string.Join(",", ReadTrees(_databasePath)));
    }

    [Fact]
    public async Task Delete_of_missing_task_reports_not_found()
    {
        DatabaseInitializer.Initialize(_databasePath);
        var store = new SqliteTaskStore(_databasePath);

        TaskDeleteResult result = await store.DeleteAsync(Guid.NewGuid());

        Assert.False(result.Deleted);
        Assert.Equal(0, result.RemovedSubtasks);
    }

    /// <summary>Создаёт базу со схемой до появления дерева задач.</summary>
    private void CreateLegacyDatabase(params (Guid Id, string CreatedAt)[] tasks)
    {
        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE tasks (
                    id          TEXT PRIMARY KEY,
                    created_at  TEXT NOT NULL,
                    updated_at  TEXT NOT NULL,
                    source      TEXT NOT NULL,
                    raw_text    TEXT NOT NULL,
                    title       TEXT NOT NULL,
                    status      TEXT NOT NULL,
                    priority    INTEGER NOT NULL DEFAULT 2,
                    workspace   TEXT NULL,
                    meta_json   TEXT NULL
                );

                CREATE INDEX ix_tasks_created ON tasks (created_at DESC);
                CREATE INDEX ix_tasks_status ON tasks (status);
                """;
            schema.ExecuteNonQuery();
        }

        foreach ((Guid id, string createdAt) in tasks)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO tasks (id, created_at, updated_at, source, raw_text, title, status, priority) " +
                "VALUES ($id, $createdAt, $createdAt, 'api', 'старый текст', 'Старая задача', 'new', 2)";
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.Parameters.AddWithValue("$createdAt", createdAt);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Строки <c>task_trees</c> в порядке идентификатора: удобно сравнивать прогоны.</summary>
    private static List<string> ReadTrees(string databasePath)
    {
        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(databasePath));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM task_trees ORDER BY id";

        var ids = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
