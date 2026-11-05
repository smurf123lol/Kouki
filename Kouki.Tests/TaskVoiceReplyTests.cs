using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;

namespace Kouki.Tests;

using CmdAddTaskCommand = Kouki.Kagedama.Commands.AddTaskCommand;
using CmdAddTaskHandler = Kouki.Kagedama.Commands.AddTaskHandler;
using CmdListTasksCommand = Kouki.Kagedama.Commands.ListTasksCommand;
using CmdListTasksHandler = Kouki.Kagedama.Commands.ListTasksHandler;
using CmdCompleteTaskCommand = Kouki.Kagedama.Commands.CompleteTaskCommand;
using CmdCompleteTaskHandler = Kouki.Kagedama.Commands.CompleteTaskHandler;
using CmdPatchTaskCommand = Kouki.Kagedama.Commands.PatchTaskCommand;
using CmdPatchTaskHandler = Kouki.Kagedama.Commands.PatchTaskHandler;

/// <summary>
/// Тесты T88: голосовые ответы (поле answer) на действия с задачами.
/// </summary>
public sealed class TaskVoiceReplyTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public TaskVoiceReplyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-voice-reply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "kouki.sqlite");
        DatabaseInitializer.Initialize(_databasePath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AddTask_returns_answer_with_recorded_title()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new CmdAddTaskHandler(store);

        JsonNode result = await handler.HandleAsync(new CmdAddTaskCommand("купить свежий хлеб"));

        Assert.True(result["created"]?.GetValue<bool>());
        Assert.Equal("Записала: купить свежий хлеб", result["answer"]?.GetValue<string>());
    }

    [Fact]
    public async Task AddTask_with_empty_text_returns_answer_with_error()
    {
        var store = new SqliteTaskStore(_databasePath);
        var handler = new CmdAddTaskHandler(store);

        JsonNode result = await handler.HandleAsync(new CmdAddTaskCommand("   "));

        Assert.Equal("Пустая фраза: нечего записывать как задачу.", result["error"]?.GetValue<string>());
        Assert.Equal("Пустая фраза: нечего записывать как задачу.", result["answer"]?.GetValue<string>());
    }

    [Fact]
    public async Task ListTasks_returns_proper_answer_for_zero_one_and_many_tasks()
    {
        var store = new SqliteTaskStore(_databasePath);
        var addHandler = new CmdAddTaskHandler(store);
        var listHandler = new CmdListTasksHandler(store);

        // 0 задач
        JsonNode emptyList = await listHandler.HandleAsync(new CmdListTasksCommand());
        Assert.Equal("У вас нет открытых задач.", emptyList["answer"]?.GetValue<string>());

        // 1 задача
        await addHandler.HandleAsync(new CmdAddTaskCommand("первая задача"));
        JsonNode oneList = await listHandler.HandleAsync(new CmdListTasksCommand());
        Assert.Equal("У вас одна задача: первая задача.", oneList["answer"]?.GetValue<string>());

        // 2 задачи
        await addHandler.HandleAsync(new CmdAddTaskCommand("вторая задача"));
        JsonNode twoList = await listHandler.HandleAsync(new CmdListTasksCommand());
        Assert.Equal("У вас 2 задачи.", twoList["answer"]?.GetValue<string>());

        // 5 задач
        await addHandler.HandleAsync(new CmdAddTaskCommand("третья задача"));
        await addHandler.HandleAsync(new CmdAddTaskCommand("четвертая задача"));
        await addHandler.HandleAsync(new CmdAddTaskCommand("пятая задача"));
        JsonNode fiveList = await listHandler.HandleAsync(new CmdListTasksCommand());
        Assert.Equal("У вас 5 задач.", fiveList["answer"]?.GetValue<string>());
    }

    [Fact]
    public async Task CompleteTask_returns_answer_and_handles_already_closed()
    {
        var store = new SqliteTaskStore(_databasePath);
        var addHandler = new CmdAddTaskHandler(store);
        var completeHandler = new CmdCompleteTaskHandler(store);

        // Пустая база
        JsonNode emptyResult = await completeHandler.HandleAsync(new CmdCompleteTaskCommand());
        Assert.Equal("Открытых задач нет: нечего закрывать.", emptyResult["answer"]?.GetValue<string>());

        // Создаем задачу
        JsonNode addResult = await addHandler.HandleAsync(new CmdAddTaskCommand("позвонить врачу"));
        string taskId = addResult["taskId"]!.GetValue<string>();

        // Закрываем задачу
        JsonNode completeResult = await completeHandler.HandleAsync(new CmdCompleteTaskCommand(taskId));
        Assert.Equal(TaskStatuses.Done, completeResult["status"]?.GetValue<string>());
        Assert.Equal("Задача завершена: позвонить врачу", completeResult["answer"]?.GetValue<string>());

        // Повторное закрытие
        JsonNode secondClose = await completeHandler.HandleAsync(new CmdCompleteTaskCommand(taskId));
        Assert.True(secondClose["alreadyClosed"]?.GetValue<bool>());
        Assert.Equal("Эта задача уже была закрыта.", secondClose["answer"]?.GetValue<string>());
    }

    [Fact]
    public async Task PatchTask_returns_answer_with_new_status()
    {
        var store = new SqliteTaskStore(_databasePath);
        var addHandler = new CmdAddTaskHandler(store);
        var patchHandler = new CmdPatchTaskHandler(store);

        JsonNode addResult = await addHandler.HandleAsync(new CmdAddTaskCommand("написать отчет"));
        string taskId = addResult["taskId"]!.GetValue<string>();

        JsonNode patchResult = await patchHandler.HandleAsync(new CmdPatchTaskCommand(taskId, Status: TaskStatuses.Done));
        Assert.Equal($"Статус задачи изменен на done: написать отчет", patchResult["answer"]?.GetValue<string>());
    }
}
