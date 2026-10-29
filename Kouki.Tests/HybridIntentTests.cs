using Kouki.Yasabi;

namespace Kouki.Tests;

/// <summary>
/// Связка «правила + модель»: правила отвечают первыми, модель подключается только на неуверенных фразах.
/// </summary>
public sealed class HybridIntentTests
{
    private sealed class CountingClassifier(IntentResult result) : IIntentClassifier
    {
        public int Calls { get; private set; }

        public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Confident_rules_do_not_touch_the_model()
    {
        var rules = new CountingClassifier(new IntentResult("add_task", 1d, Model: RuleIntentClassifier.ModelName));
        var llm = new CountingClassifier(new IntentResult("chat", 0.9, Model: LlmIntentClassifier.ModelName));
        var hybrid = new HybridIntentClassifier(rules, llm);

        IntentResult result = await hybrid.ClassifyAsync("запиши задачу купить хлеб");

        Assert.Equal("add_task", result.Intent);
        Assert.Equal(RuleIntentClassifier.ModelName, result.Model);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task Unknown_phrase_is_asked_from_the_model()
    {
        var rules = new CountingClassifier(IntentResult.Unknown(RuleIntentClassifier.ModelName));
        var llm = new CountingClassifier(new IntentResult("add_task", 0.9, Model: LlmIntentClassifier.ModelName));
        var hybrid = new HybridIntentClassifier(rules, llm);

        IntentResult result = await hybrid.ClassifyAsync("надо бы не забыть сходить в аптеку");

        Assert.Equal("add_task", result.Intent);
        Assert.Equal(LlmIntentClassifier.ModelName, result.Model);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task Weak_rules_answer_below_threshold_goes_to_the_model()
    {
        var rules = new CountingClassifier(new IntentResult("add_task", 0.5, Model: RuleIntentClassifier.ModelName));
        var llm = new CountingClassifier(new IntentResult("chat", 0.8, Model: LlmIntentClassifier.ModelName));
        var hybrid = new HybridIntentClassifier(rules, llm, fallbackBelow: 0.95d);

        IntentResult result = await hybrid.ClassifyAsync("ну не знаю");

        Assert.Equal("chat", result.Intent);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task Model_silence_keeps_the_rules_answer()
    {
        var rules = new CountingClassifier(new IntentResult("add_task", 0.5, Model: RuleIntentClassifier.ModelName));
        var llm = new CountingClassifier(IntentResult.Unknown(LlmIntentClassifier.ModelName));
        var hybrid = new HybridIntentClassifier(rules, llm);

        IntentResult result = await hybrid.ClassifyAsync("купить молоко");

        Assert.Equal("add_task", result.Intent);
        Assert.Equal(0.5d, result.Score);
        Assert.Equal(RuleIntentClassifier.ModelName, result.Model);
    }
}
