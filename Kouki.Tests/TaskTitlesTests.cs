using Kouki.Kagedama;

namespace Kouki.Tests;

/// <summary>
/// Заголовок задачи — это чистка, а не разбор фразы: императив отделяет слой распознавания.
/// </summary>
public sealed class TaskTitlesTests
{
    [Fact]
    public void Phrase_is_trimmed_and_unpunctuated()
    {
        Assert.Equal("купить хлеб", TaskTitles.Make("  купить хлеб.  "));
    }

    [Fact]
    public void Imperative_is_kept_when_body_was_not_extracted()
    {
        // Фолбэк: если слой распознавания не дал body, заголовок — вся фраза, без догадок.
        Assert.Equal("Запиши задачу купить хлеб", TaskTitles.Make("Запиши задачу купить хлеб."));
    }

    [Fact]
    public void Sentence_about_a_project_task_is_not_mangled()
    {
        // Ровно тот случай, из-за которого нельзя отрезать слова по списку.
        Assert.Equal("Задача проекта — сдать отчёт", TaskTitles.Make("Задача проекта — сдать отчёт."));
    }

    [Fact]
    public void Long_title_is_truncated()
    {
        string longPhrase = new string('а', TaskTitles.Limit + 40);

        string title = TaskTitles.Make(longPhrase);

        Assert.Equal(TaskTitles.Limit + 1, title.Length);
        Assert.EndsWith("…", title);
    }
}
