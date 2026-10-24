using Kouki.Yasabi;

namespace Kouki.Tests;

/// <summary>
/// Сравнение классификаторов: одна фраза, несколько ответов, ошибки не мешают остальным.
/// </summary>
public sealed class ClassifierCatalogTests
{
    private sealed class FixedClassifier(string intent, double score) : IIntentClassifier
    {
        public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntentResult(intent, score, Model: "fixed"));
    }

    private sealed class ThrowingClassifier : IIntentClassifier
    {
        public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("модель не загрузилась");
    }

    [Fact]
    public async Task Compares_every_classifier_on_the_same_phrase()
    {
        var catalog = new ClassifierCatalog()
            .Add("rules", new FixedClassifier("add_task", 1.0))
            .Add("bert", new FixedClassifier("list_tasks", 0.6));

        IReadOnlyList<ClassifierVerdict> verdicts = await catalog.ClassifyAsync("купить хлеб", null, 0.75);

        Assert.Equal(2, verdicts.Count);
        Assert.Contains(verdicts, v => v.Classifier == "rules" && v.Intent == "add_task" && v.Confident);
        Assert.Contains(verdicts, v => v.Classifier == "bert" && v.Intent == "list_tasks" && !v.Confident);
    }

    [Fact]
    public async Task Only_filter_narrows_the_comparison()
    {
        var catalog = new ClassifierCatalog()
            .Add("rules", new FixedClassifier("add_task", 1.0))
            .Add("bert", new FixedClassifier("add_task", 0.9));

        IReadOnlyList<ClassifierVerdict> verdicts = await catalog.ClassifyAsync("купить", ["bert"], 0.75);

        ClassifierVerdict single = Assert.Single(verdicts);
        Assert.Equal("bert", single.Classifier);
    }

    [Fact]
    public async Task Failing_classifier_does_not_break_the_others()
    {
        var catalog = new ClassifierCatalog()
            .Add("broken", new ThrowingClassifier())
            .Add("rules", new FixedClassifier("add_task", 1.0));

        IReadOnlyList<ClassifierVerdict> verdicts = await catalog.ClassifyAsync("купить", null, 0.75);

        ClassifierVerdict broken = Assert.Single(verdicts, v => v.Classifier == "broken");
        Assert.Equal(IntentResult.Unclassified, broken.Intent);
        Assert.False(broken.Confident);
        Assert.Contains("InvalidOperationException", broken.Error);

        Assert.Contains(verdicts, v => v.Classifier == "rules" && v.Confident);
    }

    [Fact]
    public void Names_and_find_are_case_insensitive()
    {
        var catalog = new ClassifierCatalog().Add("Rules", new FixedClassifier("add_task", 1.0));

        Assert.Contains("Rules", catalog.Names);
        Assert.NotNull(catalog.Find("rules"));
        Assert.Null(catalog.Find("nope"));
    }
}
