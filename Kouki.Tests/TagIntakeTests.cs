using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Yasabi;

namespace Kouki.Tests;

/// <summary>
/// Теги в приёме: от модели, порядок с текстовыми, лимиты, режимы и транзакции.
/// </summary>
public sealed class TagIntakeTests
{
    /// <summary>Классификатор, который возвращает заготовленный вердикт (в том числе с тегами).</summary>
    private sealed class StubClassifier(IntentResult result) : IIntentClassifier
    {
        public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    /// <summary>Хранилище тегов, которое всегда падает: так проверяется откат транзакции.</summary>
    private sealed class FailingTagStore : ITagStore
    {
        public Task AddAsync(
            Guid taskId,
            IEnumerable<string> tags,
            string source,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("хранилище тегов недоступно");

        public Task<IReadOnlyList<string>> ListAsync(
            Guid taskId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<Guid>> FindTasksAsync(
            string tag,
            string? status,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ListForTasksAsync(
            IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>(
                new Dictionary<Guid, IReadOnlyList<string>>());
    }

    [Fact]
    public async Task Model_tags_are_stored_with_source_model()
    {
        var classifier = new StubClassifier(
            new IntentResult("add_task", 1d, Model: "llm", Tags: new[] { "работа", "покупки" }));

        using var fixture = new TagIntakeFixture(classifier);

        IntakeResult result = await fixture.Intake.IntakeAsync("купить хлеб", TaskSources.Telegram);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        IReadOnlyList<(string Tag, string Source)> rows = await fixture.TagRowsAsync(task.Id);

        Assert.Equal(new[] { "работа", "покупки" }, result.Tags);
        Assert.Equal(new[] { "работа", "покупки" }, rows.Select(row => row.Tag));
        Assert.All(rows, row => Assert.Equal(TagSources.Model, row.Source));
    }

    [Fact]
    public async Task Text_tag_wins_over_model_tag()
    {
        var classifier = new StubClassifier(
            new IntentResult("add_task", 1d, Model: "llm", Tags: new[] { "магазин", "покупки" }));

        using var fixture = new TagIntakeFixture(classifier);

        IntakeResult result = await fixture.Intake.IntakeAsync("купить хлеб #магазин", TaskSources.Telegram);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        IReadOnlyList<(string Tag, string Source)> rows = await fixture.TagRowsAsync(task.Id);

        Assert.Equal("купить хлеб", task.RawText);
        Assert.Equal(new[] { "магазин", "покупки" }, rows.Select(row => row.Tag));
        Assert.Equal(TagSources.Text, rows[0].Source);
        Assert.Equal(TagSources.Model, rows[1].Source);
    }

    [Fact]
    public async Task Model_tags_respect_the_shared_limit()
    {
        string[] tags = Enumerable.Range(1, TagParser.MaxTags + 1).Select(index => $"тег{index}").ToArray();
        var classifier = new StubClassifier(new IntentResult("add_task", 1d, Model: "llm", Tags: tags));

        using var fixture = new TagIntakeFixture(classifier);

        IntakeResult result = await fixture.Intake.IntakeAsync("купить хлеб", TaskSources.Telegram);
        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);

        Assert.Equal(TagParser.MaxTags, result.Tags.Count);
        Assert.Equal(TagParser.MaxTags, (await fixture.TagRowsAsync(task.Id)).Count);
    }

    [Fact]
    public async Task Long_model_tag_is_truncated()
    {
        string longTag = new('а', TagParser.MaxTagLength + 8);
        var classifier = new StubClassifier(new IntentResult("add_task", 1d, Model: "llm", Tags: new[] { longTag }));

        using var fixture = new TagIntakeFixture(classifier);

        IntakeResult result = await fixture.Intake.IntakeAsync("купить хлеб", TaskSources.Telegram);
        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);

        string stored = Assert.Single((await fixture.TagRowsAsync(task.Id)).Select(row => row.Tag));
        Assert.Equal(TagParser.MaxTagLength, stored.Length);
    }

    [Fact]
    public async Task Keep_mode_stores_only_model_tags()
    {
        var classifier = new StubClassifier(
            new IntentResult("add_task", 1d, Model: "llm", Tags: new[] { "работа" }));

        using var fixture = new TagIntakeFixture(classifier, new IntakeOptions
        {
            ConfidenceThreshold = 0.75,
            DefaultSource = "test",
            TagMode = TagModes.Keep,
        });

        IntakeResult result = await fixture.Intake.IntakeAsync("#работа купить хлеб", TaskSources.Station);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        IReadOnlyList<(string Tag, string Source)> rows = await fixture.TagRowsAsync(task.Id);

        // keep отключает только вырезание: тег остаётся в тексте, а модель ставит свой.
        Assert.Contains("#работа", task.RawText, StringComparison.Ordinal);
        Assert.Equal(new[] { "работа" }, rows.Select(row => row.Tag));
        Assert.Equal(TagSources.Model, rows[0].Source);
    }

    [Fact]
    public async Task Phrase_of_tags_only_is_rejected_before_writing()
    {
        Assert.False(IntakeRequestPolicy.TryPrepare(
            "#работа #дом",
            null,
            null,
            TaskSources.Api,
            IntakeRequestPolicy.DefaultMaxTextLength,
            out _,
            out IntakeRejection? rejection));
        Assert.Equal(IntakeRequestPolicy.TagsOnlyText, rejection!.Code);

        // В keep теги не вырезаются — фраза остаётся фразой и принимается.
        Assert.True(IntakeRequestPolicy.TryPrepare(
            "#работа #дом",
            null,
            null,
            TaskSources.Api,
            IntakeRequestPolicy.DefaultMaxTextLength,
            out IntakeSpec? kept,
            out _,
            TagModes.Keep));
        Assert.Equal("#работа #дом", kept!.Text);
        Assert.Empty(kept.Tags);

        using var fixture = new TagIntakeFixture();
        await Assert.ThrowsAsync<IntakeRejectionException>(
            () => fixture.Intake.IntakeAsync("#работа #дом", TaskSources.Api));
        Assert.Equal(0, await fixture.CountAsync("tasks"));
    }

    [Fact]
    public void Intake_spec_keeps_the_original_phrase_and_cut_tags()
    {
        Assert.True(IntakeRequestPolicy.TryPrepare(
            "  купить хлеб #магазин #срочно  ",
            null,
            null,
            TaskSources.Api,
            IntakeRequestPolicy.DefaultMaxTextLength,
            out IntakeSpec? spec,
            out _));

        Assert.Equal("купить хлеб #магазин #срочно", spec!.Text);
        Assert.Equal(new[] { "магазин", "срочно" }, spec.Tags);
    }

    [Fact]
    public async Task Journal_gets_clean_phrase_and_task_link()
    {
        using var fixture = new TagIntakeFixture();

        IntakeResult result = await fixture.Intake.IntakeAsync(
            "запиши задачу купить хлеб #магазин",
            TaskSources.Telegram);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        Assert.DoesNotContain("#", task.RawText, StringComparison.Ordinal);
        Assert.Equal(new[] { "магазин" }, result.Tags);

        IReadOnlyList<ClassificationEntry> journal = await fixture.Journal.ListAsync(10, onlyUnresolved: false);
        ClassificationEntry entry = Assert.Single(journal);
        Assert.Equal("запиши задачу купить хлеб", entry.InputText);

        // Тегов нет ни в журнале, ни в мете задачи: их место — только task_tags.
        Assert.DoesNotContain("\"tags\"", task.MetaJson ?? string.Empty, StringComparison.Ordinal);

        // Связка «фраза → задача» появляется и в ветке, где задачу создала команда.
        Assert.Equal(task.Id.ToString(), await fixture.LastLinkedTaskIdAsync());
    }

    [Fact]
    public async Task Needs_review_task_gets_tags()
    {
        var classifier = new StubClassifier(IntentResult.Unknown("llm"));
        using var fixture = new TagIntakeFixture(classifier);

        IntakeResult result = await fixture.Intake.IntakeAsync("непонятная штука #работа", TaskSources.Telegram);

        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        Assert.Equal(TaskStatuses.NeedsReview, task.Status);
        Assert.Equal(new[] { "работа" }, result.Tags);

        IReadOnlyList<(string Tag, string Source)> rows = await fixture.TagRowsAsync(task.Id);
        Assert.Equal(new[] { ("работа", TagSources.Text) }, rows);
    }

    [Fact]
    public async Task Ask_branch_returns_tags_without_creating_task()
    {
        using var fixture = new TagIntakeFixture();

        IntakeResult result = await fixture.Intake.IntakeAsync("что такое энтропия #наука", "test", intentHint: "ask");

        Assert.Null(result.Task);
        Assert.Equal(new[] { "наука" }, result.Tags);
        Assert.Equal(0, await fixture.CountAsync("tasks"));
    }

    [Fact]
    public async Task Failing_tag_store_rolls_back_needs_review_branch()
    {
        var classifier = new StubClassifier(IntentResult.Unknown("llm"));
        using var fixture = new TagIntakeFixture(classifier, tags: new FailingTagStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Intake.IntakeAsync("непонятная фраза #работа", "test"));

        // Теги не записались — значит, и задачи, и действия быть не должно.
        Assert.Equal(0, await fixture.CountAsync("tasks"));
        Assert.Equal(0, await fixture.CountAsync("actions"));
    }
}
