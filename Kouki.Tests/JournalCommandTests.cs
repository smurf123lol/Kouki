using System.Text.Json.Nodes;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Чтения журналов и разметка — команды, а не прямой доступ к хранилищу.
/// Тесты дергают их через маршрутизатор, как это делают HTTP и MCP.
/// </summary>
public sealed class JournalCommandTests : IDisposable
{
    private readonly string _directory;
    private readonly ServiceProvider _provider;
    private readonly Kouki.Mugura.Dispatcher.IRouter _router;

    public JournalCommandTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        string databasePath = Path.Combine(_directory, "kouki.sqlite");

        var services = new ServiceCollection();
        services.AddKagedamaData(databasePath);
        services.AddMugura(typeof(ListActionsHandler).Assembly);
        services.AddShimiContext();
        services.AddLogging();

        _provider = services.BuildServiceProvider();

        // Схему в настоящих хостах создаёт InitializeKouki; здесь — тот же initializer.
        DatabaseInitializer.Initialize(databasePath);

        var contexts = _provider.GetRequiredService<Kouki.Shimi.Context.IContextRegistry>();
        contexts.SetDefaultContext(TestHost.Context("test"));

        _router = _provider.GetRequiredService<Kouki.Mugura.Dispatcher.IRouter>();
    }

    [Fact]
    public async Task List_actions_returns_rows_and_filters_by_task()
    {
        var tasks = _provider.GetRequiredService<ITaskStore>();
        TaskRecord task = Kouki.Kagedama.TaskFactory.Create("купить хлеб", TaskSources.Station);
        await tasks.AddAsync(task, CancellationToken.None);
        Guid taskId = task.Id;

        var actions = _provider.GetRequiredService<IActionLog>();

        await actions.RecordAsync(new ActionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            TaskId: taskId,
            ClassificationId: null,
            Source: TaskSources.Station,
            Kind: ActionKinds.Intake,
            Intent: "add_task",
            Command: "add_task",
            Outcome: ActionOutcomes.Created,
            ArgsJson: """{"text":"купить хлеб"}""",
            ResultJson: """{"created":true}"""), CancellationToken.None);

        JsonNode? all = await _router.RouteAsync("list_actions", new JsonObject { ["limit"] = 10 });
        Assert.Equal(1, all!["count"]!.GetValue<int>());
        Assert.Equal(ActionOutcomes.Created, all["actions"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal(TaskSources.Station, all["actions"]![0]!["source"]!.GetValue<string>());
        Assert.Equal("add_task", all["actions"]![0]!["command"]!.GetValue<string>());

        JsonNode? foreign = await _router.RouteAsync(
            "list_actions",
            new JsonObject { ["taskId"] = Guid.NewGuid().ToString() });

        Assert.Equal(0, foreign!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task List_classifications_can_filter_unresolved_only()
    {
        var journal = _provider.GetRequiredService<IClassificationJournal>();

        Guid confident = await journal.RecordAsync(new ClassificationRecord(
            TaskSources.Telegram, "text", "покажи мои задачи", "list_tasks", 1, "rules-v1", Confident: true, Error: null));
        Guid unsure = await journal.RecordAsync(new ClassificationRecord(
            TaskSources.Telegram, "text", "погода сегодня хорошая", "unclassified", 0, "rules-v1", Confident: false, Error: null));

        Assert.NotEqual(Guid.Empty, confident);
        Assert.NotEqual(Guid.Empty, unsure);

        JsonNode? all = await _router.RouteAsync("list_classifications", new JsonObject { ["limit"] = 10 });
        Assert.Equal(2, all!["count"]!.GetValue<int>());

        JsonNode? unresolved = await _router.RouteAsync(
            "list_classifications",
            new JsonObject { ["limit"] = 10, ["unresolved"] = true });

        Assert.Equal(1, unresolved!["count"]!.GetValue<int>());
        Assert.Equal(unsure.ToString(), unresolved["entries"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Label_rejects_blank_and_too_long_labels()
    {
        var journal = _provider.GetRequiredService<IClassificationJournal>();
        Guid id = await journal.RecordAsync(new ClassificationRecord(
            TaskSources.Api, "text", "непонятная фраза", "unclassified", 0, "rules-v1", Confident: false, Error: null));


        JsonNode? blank = await _router.RouteAsync(
            "label_classification",
            new JsonObject { ["id"] = id.ToString(), ["label"] = "   " });

        Assert.False(blank!["labeled"]!.GetValue<bool>());
        Assert.Equal("invalid", blank["reason"]!.GetValue<string>());

        JsonNode? tooLong = await _router.RouteAsync(
            "label_classification",
            new JsonObject { ["id"] = id.ToString(), ["label"] = new string('я', LabelClassificationHandler.MaxLabelLength + 1) });

        Assert.Equal("invalid", tooLong!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Label_marks_record_and_removes_it_from_unresolved()
    {
        var journal = _provider.GetRequiredService<IClassificationJournal>();
        Guid id = await journal.RecordAsync(new ClassificationRecord(
            TaskSources.Voice, "asr", "купить молоко", "unclassified", 0, "rules-v1", Confident: false, Error: null));

        JsonNode? labeled = await _router.RouteAsync(
            "label_classification",
            new JsonObject { ["id"] = id.ToString(), ["label"] = " add_task " });

        Assert.True(labeled!["labeled"]!.GetValue<bool>());
        Assert.Equal("add_task", labeled["label"]!.GetValue<string>());

        JsonNode? unresolved = await _router.RouteAsync(
            "list_classifications",
            new JsonObject { ["unresolved"] = true });

        Assert.Equal(0, unresolved!["count"]!.GetValue<int>());

        JsonNode? all = await _router.RouteAsync("list_classifications", new JsonObject { ["limit"] = 10 });
        Assert.Equal("add_task", all!["entries"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Label_unknown_record_is_not_found()
    {
        JsonNode? result = await _router.RouteAsync(
            "label_classification",
            new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["label"] = "add_task" });

        Assert.False(result!["labeled"]!.GetValue<bool>());
        Assert.Equal("not-found", result["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_task_reads_one_task_and_reports_missing()
    {
        // C-11: HTTP читал одну задачу напрямую. Теперь есть команда — проверяем оба исхода.
        var store = _provider.GetRequiredService<ITaskStore>();
        TaskRecord task = Kouki.Kagedama.TaskFactory.Create("позвонить маме", TaskSources.Api);
        await store.AddAsync(task, CancellationToken.None);

        JsonNode? found = await _router.RouteAsync("get_task", new JsonObject { ["taskId"] = task.Id.ToString() });

        Assert.True(found!["found"]!.GetValue<bool>());
        Assert.Equal("позвонить маме", found["title"]!.GetValue<string>());
        Assert.Equal("new", found["status"]!.GetValue<string>());

        JsonNode? missing = await _router.RouteAsync("get_task", new JsonObject { ["taskId"] = Guid.NewGuid().ToString() });

        Assert.False(missing!["found"]!.GetValue<bool>());
        Assert.NotNull(missing["error"]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _provider.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка под стенд: не удалилась — не повод ронять тест.
        }
    }
}
