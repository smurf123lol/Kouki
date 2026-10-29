using Kouki.Mugura.DependencyInjection;
using Kouki.Yasabi;
using Microsoft.Extensions.DependencyInjection;

using Kouki.Kagedama.Contracts;

namespace Kouki.Tests;

/// <summary>Проверки «Ясаби»: правила, журнал, маршрутизация текста в команды.</summary>
public sealed class YasabiTests
{
    [Fact]
    public async Task Rules_classify_add_task()
    {
        IntentResult result = await new RuleIntentClassifier().ClassifyAsync("запиши задачу купить хлеб");

        Assert.Equal("add_task", result.Intent);
        Assert.True(result.IsConfident(0.75));
        Assert.Equal(RuleIntentClassifier.ModelName, result.Model);
    }

    [Fact]
    public async Task Rules_classify_list_tasks()
    {
        IntentResult result = await new RuleIntentClassifier().ClassifyAsync("покажи мои задачи");

        Assert.Equal("list_tasks", result.Intent);
        Assert.True(result.IsConfident(0.75));
    }

    [Theory]
    [InlineData("сколько варить яйцо")]
    [InlineData("Что посмотреть вечером из классики")]
    [InlineData("как пройти в библиотеку")]
    [InlineData("можно ли есть после шести")]
    [InlineData("why is the sky blue")]
    public async Task Questions_are_classified_as_ask(string text)
    {
        IntentResult result = await new RuleIntentClassifier().ClassifyAsync(text);

        Assert.Equal("ask", result.Intent);
        Assert.True(result.IsConfident(0.75));
    }

    [Theory]
    [InlineData("гугол это сколько")]
    [InlineData("а почему небо синее")]
    public async Task Question_word_inside_sentence_is_ask(string text)
    {
        IntentResult result = await new RuleIntentClassifier().ClassifyAsync(text);

        Assert.Equal("ask", result.Intent);
        Assert.True(result.Score >= 0.8d);
    }
    [Fact]
    public async Task Explicit_commands_still_win_over_question_mark()
    {
        IntentResult list = await new RuleIntentClassifier().ClassifyAsync("покажи мои задачи?");
        IntentResult add = await new RuleIntentClassifier().ClassifyAsync("запиши задачу купить хлеб?");

        Assert.Equal("list_tasks", list.Intent);
        Assert.Equal("add_task", add.Intent);
    }
    [Fact]
    public async Task Unrelated_text_is_unclassified()
    {
        IntentResult result = await new RuleIntentClassifier().ClassifyAsync("погода сегодня хорошая");

        Assert.Equal(IntentResult.Unclassified, result.Intent);
        Assert.False(result.IsConfident(0.75));
        Assert.Equal(0d, result.Score);
    }

    [Fact]
    public async Task Logging_classifier_records_low_confidence_case()
    {
        var journal = new RecordingJournal();
        var options = new YasabiOptions { ConfidenceThreshold = 0.75, Source = "test" };
        var classifier = new LoggingIntentClassifier(new RuleIntentClassifier(), journal, options);

        await classifier.ClassifyAsync("погода сегодня хорошая");

        ClassificationRecord record = Assert.Single(journal.Records);
        Assert.False(record.Confident);
        Assert.Equal(IntentResult.Unclassified, record.Intent);
        Assert.Equal("text", record.InputKind);
        Assert.Equal("test", record.Source);
        Assert.Null(record.Error);
    }

    [Fact]
    public async Task Logging_classifier_records_failure()
    {
        var journal = new RecordingJournal();
        var options = new YasabiOptions();
        var classifier = new LoggingIntentClassifier(new ThrowingClassifier(), journal, options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => classifier.ClassifyAsync("что угодно"));

        ClassificationRecord record = Assert.Single(journal.Records);
        Assert.False(record.Confident);
        Assert.NotNull(record.Error);
        Assert.Contains("InvalidOperationException", record.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Intent_router_executes_registered_command()
    {
        using ServiceProvider provider = BuildProvider();
        var router = provider.GetRequiredService<IIntentRouter>();

        IntentRoute route = await router.RouteAsync("запиши задачу купить хлеб", TestHost.Context());

        Assert.True(route.Executed);
        Assert.Equal("add_task", route.CommandName);
        Assert.Null(route.Error);
        Assert.NotNull(route.Result);
        Assert.True(route.Result!["created"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Intent_router_does_not_execute_unclear_text()
    {
        using ServiceProvider provider = BuildProvider();
        var router = provider.GetRequiredService<IIntentRouter>();

        IntentRoute route = await router.RouteAsync("погода сегодня хорошая", TestHost.Context());

        Assert.False(route.Executed);
        Assert.Null(route.CommandName);
        Assert.Null(route.Error);
        Assert.Equal(IntentResult.Unclassified, route.Intent.Intent);
    }

    [Fact]
    public async Task Intent_router_reports_missing_command()
    {
        using ServiceProvider provider = BuildProvider(options =>
        {
            options.Rules.Clear();
            options.Rules.Add(IntentRule.Strong("unknown_intent", "маршрут в никуда"));
        });
        var router = provider.GetRequiredService<IIntentRouter>();

        IntentRoute route = await router.RouteAsync("маршрут в никуда", TestHost.Context());

        Assert.False(route.Executed);
        Assert.NotNull(route.Error);
        Assert.Contains("не зарегистрирована", route.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_text_is_rejected()
    {
        using ServiceProvider provider = BuildProvider();
        var router = provider.GetRequiredService<IIntentRouter>();

        IntentRoute route = await router.RouteAsync("   ");

        Assert.False(route.Executed);
        Assert.NotNull(route.Error);
        Assert.Contains("Пустая фраза", route.Error, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildProvider(Action<YasabiOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddMugura(TestHost.Assemblies);
        services.AddYasabi(configure);

        return services.BuildServiceProvider();
    }

    private sealed class RecordingJournal : IClassificationJournal
    {
        public List<ClassificationRecord> Records { get; } = new();

        public Task<Guid> RecordAsync(ClassificationRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<IReadOnlyList<ClassificationEntry>> ListAsync(int limit, bool onlyUnresolved, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ClassificationEntry>>(Array.Empty<ClassificationEntry>());

        public Task<bool> LabelAsync(Guid id, string label, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> AnnotateAudioAsync(Guid id, double? asrConfidence, string? audioPath, CancellationToken cancellationToken = default, string? source = null)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ClassificationEntry>> ListLabeledAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ClassificationEntry>>(Array.Empty<ClassificationEntry>());

        public Task<bool> AnnotateHintAsync(Guid id, string? hintedIntent, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> LinkToTaskAsync(Guid id, Guid taskId, CancellationToken cancellationToken = default)
        {
            Linked.Add((id, taskId));
            return Task.FromResult(true);
        }

        public List<(Guid Id, Guid TaskId)> Linked { get; } = new();
    }

    private sealed class ThrowingClassifier : IIntentClassifier
    {
        public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("сломался");
    }
}
