using System.Reflection;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.Attributes;

using TaskFactory = Kouki.Kagedama.TaskFactory;

namespace Kouki.Tests;

/// <summary>
/// Команды дерева задач: создание подзадачи, перенос по доске и удаление
/// в двух режимах — включая тексты отказов и запись в журнал действий.
/// </summary>
public sealed class TaskTreeCommandTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    /// <summary>Готовит временную базу под каждый тест.</summary>
    public TaskTreeCommandTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-tree-cmd-" + Guid.NewGuid().ToString("N"));
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
    public async Task Add_task_with_parent_creates_subtask_in_parent_tree()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new Kouki.Kagedama.Commands.AddTaskHandler(store);

        JsonNode parentResult = await handler.HandleAsync(
            new Kouki.Kagedama.Commands.AddTaskCommand("корень", TaskSources.Dashboard));
        Guid parentId = Guid.Parse(parentResult["taskId"]!.GetValue<string>());

        JsonNode childResult = await handler.HandleAsync(
            new Kouki.Kagedama.Commands.AddTaskCommand("шаг", TaskSources.Dashboard, parentId, TaskPriorities.High));

        Assert.Null(childResult["error"]);
        Assert.True(childResult["created"]!.GetValue<bool>());
        Assert.Equal(parentId.ToString(), childResult["parentId"]!.GetValue<string>());
        Assert.Equal(parentId.ToString(), childResult["treeId"]!.GetValue<string>());
        Assert.Equal(TaskPriorities.High, childResult["priority"]!.GetValue<int>());

        Guid childId = Guid.Parse(childResult["taskId"]!.GetValue<string>());
        TaskRecord stored = (await store.GetAsync(childId))!;
        Assert.Equal(parentId, stored.ParentId);
        Assert.Equal(parentId, stored.TreeId);
        Assert.Equal(TaskPositions.First, stored.Position);
        Assert.Equal(TaskPriorities.High, stored.Priority);
    }

    [Fact]
    public async Task Add_task_with_missing_parent_returns_human_error()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new Kouki.Kagedama.Commands.AddTaskHandler(store);

        JsonNode result = await handler.HandleAsync(
            new Kouki.Kagedama.Commands.AddTaskCommand("сирота", TaskSources.Dashboard, Guid.NewGuid()));

        Assert.Equal("Родительская задача не найдена.", result["error"]!.GetValue<string>());
        Assert.Empty(await store.ListAsync(null, 100));
    }

    [Fact]
    public async Task List_tasks_filters_by_parent_and_counts_subtasks_in_one_query()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new ListTasksHandler(store);

        TaskRecord root = TaskFactory.Create("корень", TaskSources.Dashboard);
        await store.AddAsync(root);
        TaskRecord first = TaskFactory.Create("шаг 1", TaskSources.Dashboard);
        first.ParentId = root.Id;
        first.Status = TaskStatuses.Done;
        await store.AddAsync(first);
        TaskRecord second = TaskFactory.Create("шаг 2", TaskSources.Dashboard);
        second.ParentId = root.Id;
        await store.AddAsync(second);

        JsonNode children = await handler.HandleAsync(new ListTasksCommand(ParentId: root.Id));
        Assert.Equal(2, children["count"]!.GetValue<int>());
        foreach (JsonNode? child in children["tasks"]!.AsArray())
        {
            Assert.Equal(root.Id.ToString(), child!["parentId"]!.GetValue<string>());
            Assert.Equal(0, child["subtasksCount"]!.GetValue<int>());
            Assert.Equal(0, child["subtasksDoneCount"]!.GetValue<int>());
        }

        JsonNode topLevel = await handler.HandleAsync(new ListTasksCommand(TopLevel: true));
        Assert.Equal(1, topLevel["count"]!.GetValue<int>());
        JsonNode rootJson = topLevel["tasks"]!.AsArray()[0]!;
        Assert.Null(rootJson["parentId"]);
        Assert.Equal(root.Id.ToString(), rootJson["treeId"]!.GetValue<string>());
        Assert.Equal(TaskPositions.First, rootJson["position"]!.GetValue<int>());
        Assert.Equal(2, rootJson["subtasksCount"]!.GetValue<int>());
        Assert.Equal(1, rootJson["subtasksDoneCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task List_tasks_clamps_limit_and_defaults_to_fifty()
    {
        var store = new SqliteTaskStore(_databasePath);
        for (int i = 0; i < 55; i++)
        {
            await store.AddAsync(TaskFactory.Create($"задача {i}", TaskSources.Dashboard));
        }

        var handler = new ListTasksHandler(store);

        JsonNode byDefault = await handler.HandleAsync(new ListTasksCommand());
        Assert.Equal(50, byDefault["count"]!.GetValue<int>());

        JsonNode zero = await handler.HandleAsync(new ListTasksCommand(Limit: 0));
        Assert.Equal(1, zero["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Move_task_shifts_positions_and_is_idempotent()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new MoveTaskHandler(store);

        TaskRecord first = await AddAsync(store, "первая");
        TaskRecord second = await AddAsync(store, "вторая");
        TaskRecord third = await AddAsync(store, "третья");
        TaskRecord fourth = await AddAsync(store, "четвёртая");

        JsonNode movedToStart = await handler.HandleAsync(
            new MoveTaskCommand(fourth.Id.ToString(), TaskStatuses.New, 0));
        Assert.Null(movedToStart["error"]);
        Assert.Equal(0, movedToStart["position"]!.GetValue<int>());

        List<Guid> order = await OrderAsync(store, TaskStatuses.New);
        Assert.Equal(new[] { fourth.Id, first.Id, second.Id, third.Id }, order.ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, await PositionsAsync(store, TaskStatuses.New));

        // Повторный вызов с теми же status/position ничего не двигает (§2.8).
        JsonNode again = await handler.HandleAsync(
            new MoveTaskCommand(fourth.Id.ToString(), TaskStatuses.New, 0));
        Assert.Null(again["error"]);
        Assert.Equal(order.ToArray(), (await OrderAsync(store, TaskStatuses.New)).ToArray());

        // Перенос за конец колонки ставит карточку последней.
        JsonNode toEnd = await handler.HandleAsync(
            new MoveTaskCommand(first.Id.ToString(), TaskStatuses.New, 99));
        Assert.Null(toEnd["error"]);
        Assert.Equal(
            new[] { fourth.Id, second.Id, third.Id, first.Id },
            (await OrderAsync(store, TaskStatuses.New)).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, await PositionsAsync(store, TaskStatuses.New));

        // Единственная карточка в пустой целевой колонке при position=99 остаётся на нуле.
        JsonNode alone = await handler.HandleAsync(
            new MoveTaskCommand(fourth.Id.ToString(), TaskStatuses.Done, 99));
        Assert.Null(alone["error"]);
        Assert.Equal(TaskPositions.First, alone["position"]!.GetValue<int>());
    }

    [Fact]
    public async Task Move_task_to_another_column_rebuilds_both_columns()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new MoveTaskHandler(store);

        TaskRecord first = await AddAsync(store, "первая");
        TaskRecord second = await AddAsync(store, "вторая");
        TaskRecord third = await AddAsync(store, "третья");

        JsonNode result = await handler.HandleAsync(
            new MoveTaskCommand(second.Id.ToString(), TaskStatuses.InProgress, 0));

        Assert.Null(result["error"]);
        Assert.Equal(TaskStatuses.InProgress, result["status"]!.GetValue<string>());
        Assert.Equal(new[] { first.Id, third.Id }, (await OrderAsync(store, TaskStatuses.New)).ToArray());
        // Исходная колонка не уплотняется: §2.4 требует сдвигать только целевую колонку.
        Assert.Equal(new[] { 0, 2 }, await PositionsAsync(store, TaskStatuses.New));
        Assert.Equal(new[] { second.Id }, (await OrderAsync(store, TaskStatuses.InProgress)).ToArray());
        Assert.Equal(new[] { 0 }, await PositionsAsync(store, TaskStatuses.InProgress));

        // Цель не меняется: дерево и родитель остаются прежними.
        TaskRecord stored = (await store.GetAsync(second.Id))!;
        Assert.Equal(second.TreeId, stored.TreeId);
        Assert.Null(stored.ParentId);
    }

    [Fact]
    public async Task Move_task_rejects_bad_status_and_position()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new MoveTaskHandler(store);
        TaskRecord task = await AddAsync(store, "карточка");

        JsonNode noStatus = await handler.HandleAsync(new MoveTaskCommand(task.Id.ToString(), null, 0));
        Assert.Equal("Укажите status.", noStatus["error"]!.GetValue<string>());

        JsonNode unknown = await handler.HandleAsync(
            new MoveTaskCommand(task.Id.ToString(), "когда-нибудь", 0));
        Assert.Equal("Неизвестный статус 'когда-нибудь'.", unknown["error"]!.GetValue<string>());

        foreach (int? position in new int?[] { null, -1, MoveTaskHandler.MaxPosition + 1 })
        {
            JsonNode badPosition = await handler.HandleAsync(
                new MoveTaskCommand(task.Id.ToString(), TaskStatuses.New, position));
            Assert.Equal("position: целое число от 0.", badPosition["error"]!.GetValue<string>());
        }

        JsonNode noId = await handler.HandleAsync(new MoveTaskCommand(null, TaskStatuses.New, 0));
        Assert.Equal("Не указан идентификатор задачи.", noId["error"]!.GetValue<string>());

        Guid missing = Guid.NewGuid();
        JsonNode notFound = await handler.HandleAsync(
            new MoveTaskCommand(missing.ToString(), TaskStatuses.New, 0));
        Assert.Equal($"Задача {missing} не найдена.", notFound["error"]!.GetValue<string>());

        // Ни одна ошибочная попытка не сдвинула карточку.
        Assert.Equal(new[] { task.Id }, (await OrderAsync(store, TaskStatuses.New)).ToArray());
        Assert.Equal(TaskPositions.First, (await store.GetAsync(task.Id))!.Position);
    }

    [Fact]
    public async Task Move_task_writes_command_audit()
    {
        var store = new SqliteTaskStore(_databasePath);
        var actions = new SqliteActionLog(_databasePath);
        var handler = new MoveTaskHandler(store, actions);
        TaskRecord task = await AddAsync(store, "карточка");

        JsonNode result = await handler.HandleAsync(
            new MoveTaskCommand(task.Id.ToString(), TaskStatuses.Done, 0));

        Assert.Null(result["error"]);

        IReadOnlyList<ActionRecord> records = await actions.ListAsync(10, task.Id);
        ActionRecord audit = Assert.Single(records, record => record.Command == "move_task");
        Assert.Equal(ActionKinds.Command, audit.Kind);
        Assert.Equal(ActionOutcomes.Executed, audit.Outcome);
        Assert.Equal(task.Id, audit.TaskId);
    }

    [Fact]
    public async Task Delete_cascade_removes_subtree_and_clears_references()
    {
        var store = new SqliteTaskStore(_databasePath);
        var reminders = new SqliteReminderStore(_databasePath);
        var tags = new SqliteTagStore(_databasePath);
        var actions = new SqliteActionLog(_databasePath);
        var journal = new SqliteClassificationJournal(_databasePath);

        TaskRecord root = await AddAsync(store, "корень");
        TaskRecord firstChild = await AddAsync(store, "шаг 1", root.Id);
        TaskRecord secondChild = await AddAsync(store, "шаг 2", root.Id);
        TaskRecord grandchild = await AddAsync(store, "шаг шага", firstChild.Id);

        await tags.AddAsync(firstChild.Id, new[] { "дом" }, TagSources.Text);
        ReminderRecord reminder = await reminders.AddAsync(
            "позвонить", DateTimeOffset.UtcNow.AddHours(1), null, TaskSources.Dashboard, grandchild.Id);
        Guid actionId = await actions.RecordAsync(new ActionRecord(
            Guid.NewGuid(), DateTimeOffset.UtcNow.ToString("O"), firstChild.Id, null,
            TaskSources.Dashboard, ActionKinds.Command, null, "patch_task", ActionOutcomes.Executed));
        Guid classificationId = await journal.RecordAsync(new ClassificationRecord(
            TaskSources.Dashboard, "text", "фраза", "add_task", 0.9, "rules", true, null));
        await journal.LinkToTaskAsync(classificationId, secondChild.Id);

        var handler = new DeleteTaskCascadeHandler(store);
        JsonNode result = await handler.HandleAsync(new DeleteTaskCascadeCommand(root.Id.ToString()));

        Assert.Null(result["error"]);
        Assert.True(result["deleted"]!.GetValue<bool>());
        Assert.Equal(TaskDeleteModes.Cascade, result["mode"]!.GetValue<string>());
        Assert.Equal(3, result["removedSubtasks"]!.GetValue<int>());
        Assert.Equal(root.Id.ToString(), result["taskId"]!.GetValue<string>());

        foreach (TaskRecord gone in new[] { root, firstChild, secondChild, grandchild })
        {
            Assert.Null(await store.GetAsync(gone.Id));
        }

        Assert.Empty(await tags.ListAsync(firstChild.Id));
        Assert.Empty(await reminders.ForTaskAsync(grandchild.Id));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM task_tags"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM reminders"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM task_trees"));

        // Аудит и датасет остаются: у них обнулена ссылка, а не удалена запись.
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM actions WHERE task_id IS NOT NULL"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM actions WHERE id = '{actionId}'"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM classifications WHERE task_id IS NOT NULL"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM classifications WHERE id = '{classificationId}'"));
    }

    [Fact]
    public async Task Delete_detach_keeps_children_in_the_same_tree()
    {
        var store = new SqliteTaskStore(_databasePath);
        TaskRecord root = await AddAsync(store, "корень");
        TaskRecord firstChild = await AddAsync(store, "шаг 1", root.Id);
        TaskRecord secondChild = await AddAsync(store, "шаг 2", root.Id);
        TaskRecord grandchild = await AddAsync(store, "шаг шага", firstChild.Id);

        var handler = new DeleteTaskDetachHandler(store);
        JsonNode result = await handler.HandleAsync(new DeleteTaskDetachCommand(root.Id.ToString()));

        Assert.Null(result["error"]);
        Assert.Equal(TaskDeleteModes.Detach, result["mode"]!.GetValue<string>());
        Assert.Equal(2, result["removedSubtasks"]!.GetValue<int>());
        Assert.Null(await store.GetAsync(root.Id));

        TaskRecord storedFirst = (await store.GetAsync(firstChild.Id))!;
        TaskRecord storedSecond = (await store.GetAsync(secondChild.Id))!;
        Assert.Null(storedFirst.ParentId);
        Assert.Null(storedSecond.ParentId);
        Assert.Equal(root.Id, storedFirst.TreeId);
        Assert.Equal(root.Id, storedSecond.TreeId);
        Assert.Equal(firstChild.Id, (await store.GetAsync(grandchild.Id))!.ParentId);
    }

    [Fact]
    public async Task Delete_reports_missing_task_with_human_text()
    {
        var store = new SqliteTaskStore(_databasePath);
        Guid missing = Guid.NewGuid();

        JsonNode byId = await new DeleteTaskCascadeHandler(store)
            .HandleAsync(new DeleteTaskCascadeCommand(missing.ToString()));
        Assert.Equal($"Задача {missing} не найдена.", byId["error"]!.GetValue<string>());

        JsonNode noId = await new DeleteTaskDetachHandler(store)
            .HandleAsync(new DeleteTaskDetachCommand(null));
        Assert.Equal("Не указан идентификатор задачи.", noId["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_cascade_rolls_back_when_a_delete_fails()
    {
        var store = new SqliteTaskStore(_databasePath);
        TaskRecord root = await AddAsync(store, "корень");
        TaskRecord child = await AddAsync(store, "шаг", root.Id);
        TaskRecord grandchild = await AddAsync(store, "шаг шага", child.Id);

        // Триггер падает на последней карточке поддерева: если транзакция не откатится,
        // корень и часть детей уже были бы удалены.
        Execute($"""
            CREATE TRIGGER fail_delete BEFORE DELETE ON tasks
            WHEN OLD.id = '{grandchild.Id}'
            BEGIN
                SELECT RAISE(ABORT, 'сбой удаления');
            END;
            """);

        var handler = new DeleteTaskCascadeHandler(store);

        await Assert.ThrowsAsync<SqliteException>(
            () => handler.HandleAsync(new DeleteTaskCascadeCommand(root.Id.ToString())));

        Assert.NotNull(await store.GetAsync(root.Id));
        Assert.NotNull(await store.GetAsync(child.Id));
        Assert.NotNull(await store.GetAsync(grandchild.Id));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM task_trees"));
    }

    [Fact]
    public void Delete_handlers_are_marked_as_dangerous()
    {
        CommandAttribute cascade = typeof(DeleteTaskCascadeHandler).GetCustomAttribute<CommandAttribute>()!;
        CommandAttribute detach = typeof(DeleteTaskDetachHandler).GetCustomAttribute<CommandAttribute>()!;

        Assert.Equal("delete_task_cascade", cascade.Name);
        Assert.True(cascade.RequiresConfirmation);
        Assert.Equal("delete_task_detach", detach.Name);
        Assert.True(detach.RequiresConfirmation);

        CommandAttribute move = typeof(MoveTaskHandler).GetCustomAttribute<CommandAttribute>()!;
        Assert.Equal("move_task", move.Name);
        Assert.False(move.RequiresConfirmation);
    }

    private async Task<TaskRecord> AddAsync(SqliteTaskStore store, string title, Guid? parentId = null)
    {
        TaskRecord record = TaskFactory.Create(title, TaskSources.Dashboard);
        record.ParentId = parentId;
        await store.AddAsync(record);
        return record;
    }

    private static async Task<List<Guid>> OrderAsync(SqliteTaskStore store, string status)
    {
        IReadOnlyList<TaskRecord> tasks = await store.ListAsync(status, 100);
        return tasks.Select(task => task.Id).ToList();
    }

    private static async Task<List<int>> PositionsAsync(SqliteTaskStore store, string status)
    {
        IReadOnlyList<TaskRecord> tasks = await store.ListAsync(status, 100);
        return tasks.Select(task => task.Position).ToList();
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
