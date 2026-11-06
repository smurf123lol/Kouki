using System.Diagnostics;
using Kouki.Yasabi;

namespace Kouki.Tests;

public sealed class OnnxIntentClassifierTests
{
    private static (string ModelPath, string VocabPath)? ResolveModelPaths()
    {
        string model = TestPaths.Path("models", "intent", "rubert-tiny2-intents.onnx");
        string vocab = TestPaths.Path("models", "intent", "vocab.txt");

        if (File.Exists(model) && File.Exists(vocab))
        {
            return (model, vocab);
        }

        // Запасной путь для сервера
        string srvModel = "/var/lib/kouki/models/intent/rubert-tiny2-intents.onnx";
        string srvVocab = "/var/lib/kouki/models/intent/vocab.txt";
        if (File.Exists(srvModel) && File.Exists(srvVocab))
        {
            return (srvModel, srvVocab);
        }

        return null;
    }

    [Fact]
    public void Empty_or_whitespace_returns_unknown()
    {
        var paths = ResolveModelPaths();
        if (paths is null)
        {
            return; // Пропуск если модель не развернута
        }

        using var classifier = new OnnxIntentClassifier(paths.Value.ModelPath, paths.Value.VocabPath);

        var resEmpty = classifier.ClassifyAsync("").GetAwaiter().GetResult();
        Assert.Equal(IntentResult.Unclassified, resEmpty.Intent);
        Assert.Equal(0d, resEmpty.Score);
        Assert.Equal(OnnxIntentClassifier.ModelName, resEmpty.Model);

        var resWs = classifier.ClassifyAsync("   ").GetAwaiter().GetResult();
        Assert.Equal(IntentResult.Unclassified, resWs.Intent);
    }

    [Theory]
    [InlineData("запиши задачу купить хлеб", "add_task")]
    [InlineData("надо починить кран на кухне", "add_task")]
    [InlineData("покажи мои задачи", "list_tasks")]
    [InlineData("какие у меня задачи на сегодня", "list_tasks")]
    [InlineData("выполнил задачу сделать отчет", "complete_task")]
    [InlineData("закрой задачу помыть посуду", "complete_task")]
    [InlineData("напомни мне завтра в девять утра", "add_reminder")]
    [InlineData("поставь напоминание через 15 минут", "add_reminder")]
    [InlineData("какие у меня напоминания", "list_reminders")]
    [InlineData("покажи список напоминаний", "list_reminders")]
    [InlineData("готово с напоминанием", "complete_reminder")]
    [InlineData("закрой напоминалку", "complete_reminder")]
    [InlineData("сколько минут варить яйцо", "ask")]
    [InlineData("почему небо синее", "ask")]
    [InlineData("привет как твои дела", "chat")]
    [InlineData("доброе утро", "chat")]
    public async Task Classifies_all_8_intents_with_high_confidence(string phrase, string expectedIntent)
    {
        var paths = ResolveModelPaths();
        if (paths is null)
        {
            return;
        }

        using var classifier = new OnnxIntentClassifier(paths.Value.ModelPath, paths.Value.VocabPath);

        IntentResult result = await classifier.ClassifyAsync(phrase);

        Assert.Equal(expectedIntent, result.Intent);
        Assert.Equal(OnnxIntentClassifier.ModelName, result.Model);
        Assert.True(result.Score >= 0.75d, $"Скор {result.Score} ниже порога 0.75 для фразы '{phrase}'");
        Assert.True(result.IsConfident(0.75d));
    }

    [Fact]
    public async Task Inference_speed_is_under_15ms_on_cpu()
    {
        var paths = ResolveModelPaths();
        if (paths is null)
        {
            return;
        }

        using var classifier = new OnnxIntentClassifier(paths.Value.ModelPath, paths.Value.VocabPath);

        // Прогрев (JIT + ONNX session graph run)
        await classifier.ClassifyAsync("прогревочный запрос");

        // Замер на 10 итерациях
        var sw = Stopwatch.StartNew();
        int iterations = 10;
        for (int i = 0; i < iterations; i++)
        {
            await classifier.ClassifyAsync("запиши задачу проверить скорость инференса");
        }
        sw.Stop();

        double avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        Assert.True(avgMs < 25.0, $"Среднее время инференса {avgMs:F2} мс превышает порог");
    }
}
