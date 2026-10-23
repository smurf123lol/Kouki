using Kouki.Kagedama.Api;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Mugura.Registry;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Yasabi;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Kouki.Kagedama.Contracts;

namespace Kouki.Tests;

/// <summary>Проверки сервера: приём фраз, команды над задачами, журнал распознавания.</summary>
public sealed class IntakeTests
{
    [Fact]
    public async Task Intake_of_add_task_goes_through_the_dispatcher()
    {
        using var server = new ServerFixture();

        IntakeResult result = await server.Intake.IntakeAsync("запиши задачу купить хлеб", "telegram");

        // Приём не создаёт задачу сам: он только распознаёт интент и подставляет слоты.
        Assert.True(result.AutoExecuted, $"error={result.Route.Error}; intent={result.Route.Intent.Intent}@{result.Route.Intent.Score}");
        Assert.Equal("add_task", result.Route.CommandName);
        Assert.Equal(IntakeOutcomes.Created, result.Outcome);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        Assert.Equal(TaskStatuses.New, task.Status);

        // Хендлер про распознавание не знает — мету дописывает приём после диспатча.
        Assert.NotNull(task.MetaJson);
        Assert.Contains("\"intent\":\"add_task\"", task.MetaJson, StringComparison.Ordinal);
        Assert.Contains("\"command\":\"add_task\"", task.MetaJson, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"created\"", task.MetaJson, StringComparison.Ordinal);

        TaskRecord? stored = await server.Store.GetAsync(task.Id);
        Assert.NotNull(stored);
        Assert.Equal("telegram", stored!.Source);
        Assert.Contains("купить хлеб", stored.RawText, StringComparison.Ordinal);
        Assert.Equal(task.MetaJson, stored.MetaJson);

        // В журнале распознавания должен остаться настоящий источник, а не «api» по умолчанию.
        IReadOnlyList<ClassificationEntry> journalEntries = await server.Journal.ListAsync(10, false, CancellationToken.None);
        Assert.Equal("telegram", journalEntries[0].Source);

        IReadOnlyList<ClassificationEntry> journal = await server.Journal.ListAsync(10, onlyUnresolved: false);
        ClassificationEntry record = Assert.Single(journal);
        Assert.Equal("add_task", record.Intent);
        Assert.True(record.Confident);

        // Одна запись журнала действий: она знает и задачу, и запись распознавания.
        IReadOnlyList<ActionRecord> actions = await server.Actions.ListAsync(10);
        ActionRecord action = Assert.Single(actions);
        Assert.Equal(ActionOutcomes.Created, action.Outcome);
        Assert.Equal("add_task", action.Command);
        Assert.Equal(task.Id, action.TaskId);
        Assert.Equal(record.Id, action.ClassificationId);
    }

    [Fact]
    public async Task Session_hint_makes_the_phrase_a_task_as_is()
    {
        using var server = new ServerFixture();

        // Станция шлёт intent=add_task: фраза — уже задача, слова из неё не вырезаются.
        IntakeResult result = await server.Intake.IntakeAsync(
            "Запиши задачу купить хлеб",
            "station",
            intentHint: "add_task");

        Assert.Equal("add_task", result.Route.Intent.Intent);
        Assert.Equal(IntakeOutcomes.Created, result.Outcome);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        Assert.Equal("Запиши задачу купить хлеб", task.Title);
    }

    [Fact]
    public async Task Session_hint_wins_over_rules()
    {
        using var server = new ServerFixture();

        // Свободный текст в сессии записи становится задачей, даже если правилам он не знаком.
        IntakeResult result = await server.Intake.IntakeAsync("молоко и хлеб", "station", intentHint: "add_task");

        Assert.Equal(IntakeOutcomes.Created, result.Outcome);
        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        Assert.Equal("молоко и хлеб", task.Title);
    }

    [Fact]
    public async Task List_tasks_command_returns_stored_tasks()
    {
        using var server = new ServerFixture();
        await server.Intake.IntakeAsync("запиши задачу купить хлеб", "api");

        IntakeResult result = await server.Intake.IntakeAsync("покажи мои задачи", "api");

        Assert.True(result.AutoExecuted, $"error={result.Route.Error}; intent={result.Route.Intent.Intent}@{result.Route.Intent.Score}");
        Assert.Equal("list_tasks", result.Route.CommandName);
        Assert.Equal(IntakeOutcomes.Executed, result.Outcome);
        Assert.Null(result.Task);

        var payload = Assert.IsType<System.Text.Json.Nodes.JsonObject>(result.Route.Result);
        Assert.True(payload["count"]!.GetValue<int>() >= 1);
    }


    [Fact]
    public async Task Command_action_keeps_args_and_result_without_task()
    {
        using var server = new ServerFixture();
        await server.Intake.IntakeAsync("запиши задачу купить хлеб", "api");

        IntakeResult result = await server.Intake.IntakeAsync("покажи мои задачи", "api");
        Assert.Null(result.Task);

        IReadOnlyList<ActionRecord> actions = await server.Actions.ListAsync(10);
        Assert.Equal(2, actions.Count);   // приём задачи + исполнение команды
        ActionRecord action = Assert.Single(actions, a => a.Outcome == ActionOutcomes.Executed);

        Assert.Equal(ActionKinds.Intake, action.Kind);
        Assert.Equal(ActionOutcomes.Executed, action.Outcome);
        Assert.Null(action.TaskId);
        Assert.Equal("list_tasks", action.Command);
        Assert.NotNull(action.ClassificationId);
        Assert.Contains("\"count\"", action.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_review_action_is_stored_with_error()
    {
        using var server = new ServerFixture();

        await server.Intake.IntakeAsync("погода сегодня хорошая", "api");

        IReadOnlyList<ActionRecord> actions = await server.Actions.ListAsync(10);
        ActionRecord action = Assert.Single(actions);

        Assert.Equal(ActionOutcomes.NeedsReview, action.Outcome);
        Assert.NotNull(action.TaskId);

        IReadOnlyList<TaskRecord> tasks = await server.Store.ListAsync(TaskStatuses.NeedsReview, 10);
        Assert.Single(tasks);
    }
    [Fact]
    public async Task Command_phrases_do_not_create_tasks()
    {
        using var server = new ServerFixture();
        await server.Intake.IntakeAsync("запиши задачу купить хлеб", "api");

        await server.Intake.IntakeAsync("покажи мои задачи", "api");
        await server.Intake.IntakeAsync("готово задачу", "api");

        IReadOnlyList<TaskRecord> tasks = await server.Store.ListAsync(null, 50);
        Assert.Single(tasks);
        Assert.Contains("купить хлеб", tasks[0].RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_task_closes_latest_open_task()
    {
        using var server = new ServerFixture();
        IntakeResult created = await server.Intake.IntakeAsync("запиши задачу позвонить маме", "api");
        TaskRecord task = Assert.IsType<TaskRecord>(created.Task);
        Assert.Equal(TaskStatuses.New, task.Status);

        IntakeResult closed = await server.Intake.IntakeAsync("готово задачу", "api");

        Assert.True(closed.AutoExecuted, $"error={closed.Route.Error}; intent={closed.Route.Intent.Intent}@{closed.Route.Intent.Score}");
        Assert.Equal("complete_task", closed.Route.CommandName);
        Assert.Null(closed.Task);

        TaskRecord? stored = await server.Store.GetAsync(task.Id);
        Assert.NotNull(stored);
        Assert.Equal(TaskStatuses.Done, stored!.Status);
    }

    [Fact]
    public async Task Unclear_text_goes_to_review_and_is_logged()
    {
        using var server = new ServerFixture();

        IntakeResult result = await server.Intake.IntakeAsync("погода сегодня хорошая", "api");

        Assert.False(result.AutoExecuted);
        Assert.Equal(IntakeOutcomes.NeedsReview, result.Outcome);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        Assert.Equal(TaskStatuses.NeedsReview, task.Status);

        IReadOnlyList<ClassificationEntry> unresolved = await server.Journal.ListAsync(10, onlyUnresolved: true);
        ClassificationEntry record = Assert.Single(unresolved);
        Assert.Equal(IntentResult.Unclassified, record.Intent);
        Assert.False(record.Confident);
        Assert.Null(record.Label);
    }

    [Fact]
    public async Task Task_store_roundtrip()
    {
        using var server = new ServerFixture();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var record = new TaskRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O"),
            Source = "dashboard",
            RawText = "проверить почту",
            Title = "Проверить почту",
            Status = TaskStatuses.New,
            Priority = 2,
        };

        await server.Store.AddAsync(record);

        TaskRecord? loaded = await server.Store.GetAsync(record.Id);
        Assert.NotNull(loaded);
        Assert.Equal(TaskStatuses.New, loaded!.Status);

        IReadOnlyList<TaskRecord> listed = await server.Store.ListAsync(TaskStatuses.New, 10);
        Assert.Single(listed);

        bool updated = await server.Store.UpdateAsync(record.Id, TaskStatuses.Done, "Проверить почту (готово)", 1);
        Assert.True(updated);

        TaskRecord? afterUpdate = await server.Store.GetAsync(record.Id);
        Assert.NotNull(afterUpdate);
        Assert.Equal(TaskStatuses.Done, afterUpdate!.Status);
        Assert.Equal(1, afterUpdate.Priority);
        Assert.Contains("готово", afterUpdate.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journal_label_marks_record()
    {
        using var server = new ServerFixture();

        Guid id = await server.Journal.RecordAsync(new ClassificationRecord(
            Source: "api",
            InputKind: "text",
            InputText: "непонятная фраза",
            Intent: IntentResult.Unclassified,
            Score: 0d,
            Model: RuleIntentClassifier.ModelName,
            Confident: false,
            Error: null));

        Assert.True(await server.Journal.LabelAsync(id, "add_task"));

        IReadOnlyList<ClassificationEntry> unresolved = await server.Journal.ListAsync(10, onlyUnresolved: true);
        Assert.Empty(unresolved);

        IReadOnlyList<ClassificationEntry> all = await server.Journal.ListAsync(10, onlyUnresolved: false);
        ClassificationEntry entry = Assert.Single(all);
        Assert.Equal("add_task", entry.Label);
        Assert.NotNull(entry.LabeledAt);
    }

    private sealed class ServerFixture : IDisposable
    {
        public ServerFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "kouki-tests-" + Guid.NewGuid().ToString("N"));
            DatabasePath = Path.Combine(DirectoryPath, "kouki.sqlite");
            DatabaseInitializer.Initialize(DatabasePath);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMugura(typeof(Kouki.Ubusuna.Commands.PingHandler).Assembly, typeof(ListTasksHandler).Assembly);
            services.AddShimiContext();
            services.AddYasabi(options => options.Source = "test");
            services.AddKagedamaData(DatabasePath);
            services.AddSingleton(sp => new TaskIntakeService(
                sp.GetRequiredService<IIntentClassifier>(),
                sp.GetRequiredService<IIntentRouter>(),
                sp.GetRequiredService<ITaskStore>(),
                sp.GetRequiredService<IActionLog>(),
                sp.GetRequiredService<IContextRegistry>(),
                new IntakeOptions
                {
                    ConfidenceThreshold = 0.75,
                    DefaultSource = "test",
                },
                sp.GetRequiredService<IClassificationJournal>()));

            Provider = services.BuildServiceProvider();
            Provider.GetRequiredService<IContextRegistry>().SetDefaultContext(TestHost.Context("server"));
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public ServiceProvider Provider { get; }

        public TaskIntakeService Intake => Provider.GetRequiredService<TaskIntakeService>();

        public ITaskStore Store => Provider.GetRequiredService<ITaskStore>();

        public IClassificationJournal Journal => Provider.GetRequiredService<IClassificationJournal>();

        public IActionLog Actions => Provider.GetRequiredService<IActionLog>();

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Файл мог остаться занятым на Windows — временный каталог не критичен.
            }
        }
    }
}
