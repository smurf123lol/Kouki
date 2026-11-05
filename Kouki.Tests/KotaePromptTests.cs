using Kouki.Kagedama.Contracts;
using Kouki.Kotae;
using Xunit;

namespace Kouki.Tests;

/// <summary>Подсказка и уборка ответа: дата в системном сообщении, markdown не читается вслух.</summary>
public sealed class KotaePromptTests
{
    [Fact]
    public void Prompt_carries_today_date_and_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Asia/Yekaterinburg", TimeSpan.FromHours(5), "Екатеринбург", "Екатеринбург");
        var now = new DateTimeOffset(2026, 10, 29, 18, 30, 0, TimeSpan.Zero);

        var prompt = KotaePrompt.BuildSystem("Ты — Kouki.", now, zone);

        Assert.Contains("29 октября 2026", prompt);
        Assert.Contains("23:30", prompt);
        Assert.Contains("Asia/Yekaterinburg", prompt);
    }

    [Fact]
    public void Clean_takes_markdown_and_line_breaks_out()
    {
        var answer = "**Ответ:** `3–4` минуты.\n\nПодробнее: [ссылка](https://example.com/page) — по вкусу.";

        var clean = KotaePrompt.Clean(answer);

        Assert.Equal("Ответ: 3–4 минуты. Подробнее: ссылка — по вкусу.", clean);
    }

    [Fact]
    public void Clean_cuts_long_answer_at_sentence_end()
    {
        var answer = string.Concat(Enumerable.Repeat("Это длинное предложение про готовку яиц. ", 20));

        var clean = KotaePrompt.Clean(answer);

        Assert.True(clean.Length <= KotaePrompt.MaxAnswerChars + 1);
        Assert.EndsWith(".", clean);
    }

    [Fact]
    public void Clean_returns_empty_for_silence()
    {
        Assert.Equal(string.Empty, KotaePrompt.Clean(null));
        Assert.Equal(string.Empty, KotaePrompt.Clean("   \n "));
    }

    [Fact]
    public void CleanHistory_returns_null_when_no_context()
    {
        Assert.Null(KotaePrompt.CleanHistory(null));
        Assert.Null(KotaePrompt.CleanHistory(AnswerContext.Empty));
        Assert.Null(KotaePrompt.CleanHistory(new AnswerContext("  ", "ответ")));
        Assert.Null(KotaePrompt.CleanHistory(new AnswerContext("вопрос", "  ")));
    }

    [Fact]
    public void CleanHistory_cleans_and_trims_turn()
    {
        var context = new AnswerContext(
            "  Сколько варить пельмени?  ",
            "**Варить** 7 минут. Подробнее: [ссылка](http://example.com)");

        var history = KotaePrompt.CleanHistory(context);

        Assert.NotNull(history);
        Assert.Equal("Сколько варить пельмени?", history.Value.Question);
        Assert.Equal("Варить 7 минут. Подробнее: ссылка", history.Value.Answer);
    }

    [Fact]
    public void FormatHistoryText_formats_both_turns()
    {
        var context = new AnswerContext("Сколько варить пельмени?", "Варить 7 минут.");
        var text = KotaePrompt.FormatHistoryText(context);

        Assert.Equal("Предыдущий вопрос: Сколько варить пельмени?\nПредыдущий ответ: Варить 7 минут.", text);
    }
}
