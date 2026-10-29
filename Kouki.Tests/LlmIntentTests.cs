using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Kouki.Kagedama.Contracts;
using Kouki.Mugura.DependencyInjection;
using Kouki.Yasabi;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Проверки классификатора на языковой модели: разбор ответа, отказоустойчивость
/// и состав каталога сравнения роутеров.
/// </summary>
public sealed class LlmIntentTests
{
    [Fact]
    public void Reply_is_parsed_into_intent_and_confidence()
    {
        const string body = """
            {"choices":[{"message":{"content":"{\"intent\": \"add_task\", \"confidence\": 0.93}"}}]}
            """;

        IntentResult result = LlmIntentClassifier.Parse(body, LlmIntentClassifier.DefaultIntents);

        Assert.Equal("add_task", result.Intent);
        Assert.Equal(0.93d, result.Score, 3);
        Assert.Equal(LlmIntentClassifier.ModelName, result.Model);
    }

    [Fact]
    public void Markdown_fence_around_json_is_tolerated()
    {
        const string body = """
            {"choices":[{"message":{"content":"```json\n{\"intent\":\"list_tasks\",\"confidence\":1}\n```"}}]}
            """;

        Assert.Equal("list_tasks", LlmIntentClassifier.Parse(body, LlmIntentClassifier.DefaultIntents).Intent);
    }

    [Fact]
    public void Unknown_intent_and_garbage_become_unclassified()
    {
        const string unknown = """
            {"choices":[{"message":{"content":"{\"intent\":\"shopping\",\"confidence\":0.9}"}}]}
            """;
        const string garbage = """
            {"choices":[{"message":{"content":"Не могу ответить"}}]}
            """;

        Assert.Equal(IntentResult.Unclassified, LlmIntentClassifier.Parse(unknown, LlmIntentClassifier.DefaultIntents).Intent);
        Assert.Equal(IntentResult.Unclassified, LlmIntentClassifier.Parse(garbage, LlmIntentClassifier.DefaultIntents).Intent);
    }

    [Fact]
    public async Task Model_failure_does_not_break_intake()
    {
        using var http = new HttpClient(new FailingHandler());
        var classifier = new LlmIntentClassifier(http, new LlmIntentOptions { Enabled = true });

        IntentResult result = await classifier.ClassifyAsync("запиши задачу купить хлеб");

        Assert.Equal(IntentResult.Unclassified, result.Intent);
        Assert.Equal(LlmIntentClassifier.ModelName, result.Model);
    }

    [Fact]
    public void Catalog_contains_model_only_when_it_is_enabled()
    {
        IServiceProvider off = new ServiceCollection().AddMugura().AddYasabi().BuildServiceProvider();
        IServiceProvider on = new ServiceCollection()
            .AddMugura()
            .AddYasabi(options => options.Llm = new LlmIntentOptions { Enabled = true })
            .BuildServiceProvider();

        Assert.DoesNotContain(LlmIntentClassifier.ModelName, off.GetRequiredService<IClassifierCatalog>().Names);
        Assert.Contains(LlmIntentClassifier.ModelName, on.GetRequiredService<IClassifierCatalog>().Names);
    }

    [Fact]
    public void Dataset_export_skips_unlabeled_and_keeps_text()
    {
        ClassificationEntry[] entries =
        [
            new() { InputText = "купить хлеб", Label = "add_task", Source = "telegram", InputKind = "text" },
            new() { InputText = "тишина", Label = null, Source = "station", InputKind = "asr" },
            new() { InputText = string.Empty, Label = "ask", Source = "api", InputKind = "text" },
        ];

        string jsonl = DatasetExport.ToJsonLines(entries);
        string[] lines = jsonl.Split('\n');

        Assert.Single(lines);
        JsonNode line = JsonNode.Parse(lines[0])!;
        Assert.Equal("купить хлеб", line["text"]!.GetValue<string>());
        Assert.Equal("add_task", line["intent"]!.GetValue<string>());
        Assert.Equal(1, DatasetExport.Count(entries));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("model is loading", Encoding.UTF8, "text/plain"),
            });
    }
}
