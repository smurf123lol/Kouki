using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Yasabi;

namespace Kouki.Tests;

public sealed class AsyncTaggingTests
{
    private sealed class StubClassifier(IntentResult result) : IIntentClassifier
    {
        public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class DelayedTagStore : ITagStore
    {
        private readonly ITagStore _inner;
        private readonly int _delayMs;

        public DelayedTagStore(ITagStore inner, int delayMs)
        {
            _inner = inner;
            _delayMs = delayMs;
        }

        public async Task AddAsync(Guid taskId, IEnumerable<string> tags, string source, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            await _inner.AddAsync(taskId, tags, source, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<string>> ListAsync(Guid taskId, CancellationToken cancellationToken = default)
            => _inner.ListAsync(taskId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ListForTasksAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
            => _inner.ListForTasksAsync(ids, cancellationToken);

        public Task<IReadOnlyList<Guid>> FindTasksAsync(string tag, string? status, int limit, CancellationToken cancellationToken = default)
            => _inner.FindTasksAsync(tag, status, limit, cancellationToken);
    }

    [Fact]
    public async Task Intake_returns_immediately_and_attaches_model_tags_in_background()
    {
        var classifier = new StubClassifier(
            new IntentResult("add_task", 1d, Model: "llm", Tags: new[] { "молоко", "магазин" }));

        var options = new IntakeOptions
        {
            ConfidenceThreshold = 0.75,
            DefaultSource = "test",
            AsyncModelTagging = true,
        };

        using var fixture = new TagIntakeFixture(classifier, options);

        // Приём возвращает результат мгновенно
        IntakeResult result = await fixture.Intake.IntakeAsync("купить молоко", TaskSources.Telegram);
        TaskRecord task = Assert.IsType<TaskRecord>(result.Task);
        Assert.NotNull(task);

        // Ждём завершения фоновой записи тегов
        for (int i = 0; i < 50; i++)
        {
            var rows = await fixture.TagRowsAsync(task.Id);
            if (rows.Count == 2)
            {
                Assert.Equal(new[] { "молоко", "магазин" }, rows.Select(r => r.Tag));
                Assert.All(rows, r => Assert.Equal(TagSources.Model, r.Source));
                return;
            }
            await Task.Delay(20);
        }

        var finalRows = await fixture.TagRowsAsync(task.Id);
        Assert.Equal(2, finalRows.Count);
    }
}
