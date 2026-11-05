using System.Text.Json.Nodes;
using Kouki.Kagedama;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kouki.Tests;

public sealed class QuestionFollowupAndActionSearchTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteSession _session;
    private readonly SqliteActionLog _actionLog;

    public QuestionFollowupAndActionSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kouki-acttests-" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "test.sqlite");
        DatabaseInitializer.Initialize(_dbPath);
        _session = new SqliteSession(_dbPath);
        _actionLog = new SqliteActionLog(_session);
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
    public async Task ActionLog_searches_by_query_across_multiple_fields()
    {
        await _actionLog.RecordAsync(new ActionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            TaskId: null,
            ClassificationId: null,
            Source: "station",
            Kind: "command",
            Intent: "add_task",
            Command: "add_task",
            Outcome: "created",
            ArgsJson: "{\"text\":\"купить батон\"}",
            ResultJson: "{\"taskId\":\"111\"}"));

        await _actionLog.RecordAsync(new ActionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            TaskId: null,
            ClassificationId: null,
            Source: "telegram",
            Kind: "command",
            Intent: "ask",
            Command: "ask",
            Outcome: "executed",
            ArgsJson: "{\"text\":\"какой рецепт пирога\"}",
            ResultJson: "{\"answer\":\"яблочный пирог\"}"));

        await _actionLog.RecordAsync(new ActionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            TaskId: null,
            ClassificationId: null,
            Source: "api",
            Kind: "command",
            Intent: "delete_task",
            Command: "delete_task",
            Outcome: "failed",
            Error: "task-not-found"));

        // Поиск по слову из текста аргументов
        IReadOnlyList<ActionRecord> r1 = await _actionLog.ListAsync(10, query: "батон");
        Assert.Single(r1);
        Assert.Equal("add_task", r1[0].Command);

        // Поиск по ответу в результатах
        IReadOnlyList<ActionRecord> r2 = await _actionLog.ListAsync(10, query: "яблочный");
        Assert.Single(r2);
        Assert.Equal("telegram", r2[0].Source);

        // Поиск по тексту ошибки
        IReadOnlyList<ActionRecord> r3 = await _actionLog.ListAsync(10, query: "task-not-found");
        Assert.Single(r3);
        Assert.Equal("failed", r3[0].Outcome);

        // Несуществующий запрос
        IReadOnlyList<ActionRecord> r4 = await _actionLog.ListAsync(10, query: "несуществующее_слово_123");
        Assert.Empty(r4);
    }

    [Fact]
    public async Task Followup_dialogue_phrase_creates_task_from_previous_question()
    {
        using var server = new IntakeTests.ServerFixture();

        // 1. Задаем вопрос от станции
        IntakeResult askResult = await server.Intake.IntakeAsync(
            "Сколько варить картошку?",
            "station",
            intentHint: "ask");

        Assert.Equal(ActionOutcomes.Executed, askResult.Outcome);
        Assert.Null(askResult.Task);

        // Проверяем, что в вопросах появился вопрос
        IReadOnlyList<QuestionRecord> questions = await server.Questions.ListAsync(5);
        Assert.Single(questions);
        Assert.Equal("Сколько варить картошку?", questions[0].Question);

        // Сохраняем ход диалога в память состояния (как делает AskHandler при наличии ответчика)
        IStateStore stateStore = server.Provider.GetRequiredService<IStateStore>();
        await stateStore.SetAsync("dialogue:last:station", new LastDialogueTurn("Сколько варить картошку?", "Примерно 20 минут.", DateTimeOffset.UtcNow));

        // 2. В follow-up окне станции пользователь говорит «запиши это в задачи»
        IntakeResult saveResult = await server.Intake.IntakeAsync(
            "запиши это в задачи",
            "station",
            continueDialogue: true);

        Assert.Equal(ActionOutcomes.Created, saveResult.Outcome);
        Assert.NotNull(saveResult.Task);
        Assert.Equal("Сколько варить картошку", saveResult.Task.Title);

        // Проверяем, что созданная задача связалась с вопросом
        QuestionRecord? updatedQuestion = await server.Questions.GetAsync(questions[0].Id);
        Assert.NotNull(updatedQuestion);
        Assert.Equal(saveResult.Task.Id, updatedQuestion.TaskId);
    }
}
