using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;

namespace Kouki.Tests;

/// <summary>
/// Теги задачи: скрапинг из фразы и отдельное хранилище <c>task_tags</c>.
/// </summary>
public sealed class TagTests : IDisposable
{
    private readonly string _directory;

    /// <summary>Готовит временную базу.</summary>
    public TagTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-tags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DatabaseInitializer.Initialize(Path.Combine(_directory, "kouki.sqlite"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог мог остаться занятым — тест от этого не зависит.
        }
    }

    [Fact]
    public void Tag_is_cut_out_of_the_phrase()
    {
        (string text, IReadOnlyList<string> tags) = TagParser.Split("купить хлеб #магазин");

        Assert.Equal("купить хлеб", text);
        Assert.Equal(new[] { "магазин" }, tags);
    }

    [Fact]
    public void Keep_mode_leaves_tags_inside_the_text()
    {
        (string text, IReadOnlyList<string> tags) = TagParser.Split("купить хлеб #магазин", TagModes.Keep);

        Assert.Equal("купить хлеб #магазин", text);
        Assert.Empty(tags);
    }

    [Fact]
    public void Bracketed_and_duplicated_tags_are_normalized()
    {
        (string text, IReadOnlyList<string> tags) = TagParser.Split("[Срочно] позвонить маме #работа #РАБОТА");

        Assert.Equal("позвонить маме", text);
        Assert.Equal(new[] { "срочно", "работа" }, tags);
    }

    [Fact]
    public void Phrase_of_tags_only_has_empty_text()
    {
        (string text, IReadOnlyList<string> tags) = TagParser.Split("#работа #дом");

        Assert.Equal(string.Empty, text);
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public async Task Tags_are_stored_and_found_by_task()
    {
        string path = Path.Combine(_directory, "kouki.sqlite");
        SqliteTaskStore tasks = new(new SqliteSession(path));
        SqliteTagStore tags = new(path);

        TaskRecord task = Kouki.Kagedama.TaskFactory.Create("купить хлеб", TaskSources.Dashboard);
        await tasks.AddAsync(task);
        await tags.AddAsync(task.Id, new[] { "магазин", "дом" }, TagSources.Text);
        await tags.AddAsync(task.Id, new[] { "магазин" }, TagSources.Model); // повтор — no-op

        IReadOnlyList<string> listed = await tags.ListAsync(task.Id);
        IReadOnlyList<Guid> found = await tags.FindTasksAsync("магазин", null, 10);

        Assert.Equal(new[] { "магазин", "дом" }, listed);
        Assert.Equal(task.Id, Assert.Single(found));
    }

    [Fact]
    public async Task Tag_status_and_limit_are_applied_together()
    {
        string path = Path.Combine(_directory, "kouki.sqlite");
        SqliteTaskStore tasks = new(new SqliteSession(path));
        SqliteTagStore tags = new(path);

        // Нужная задача создана первой: если LIMIT отсекает раньше фильтра статуса, она выпадает.
        TaskRecord wanted = Kouki.Kagedama.TaskFactory.Create("ответить на письмо", TaskSources.Api);
        await tasks.AddAsync(wanted);
        await tags.AddAsync(wanted.Id, new[] { "работа" }, TagSources.Text);

        foreach (string title in new[] { "старый отчёт", "созвон" })
        {
            TaskRecord finished = Kouki.Kagedama.TaskFactory.Create(title, TaskSources.Api);
            await tasks.AddAsync(finished);
            await tags.AddAsync(finished.Id, new[] { "работа" }, TagSources.Text);
            Assert.True(await tasks.UpdateAsync(finished.Id, TaskStatuses.Done, null, null));
        }

        // Предел считается по строкам, уже отфильтрованным по тегу и статусу.
        IReadOnlyList<Guid> found = await tags.FindTasksAsync("работа", TaskStatuses.New, 2);

        Assert.Equal(wanted.Id, Assert.Single(found));

        // Без фильтра статуса тот же предел отдаёт две самые новые задачи: выборка не пустая.
        Assert.Equal(2, (await tags.FindTasksAsync("работа", null, 2)).Count);
    }

    [Fact]
    public void Unknown_mode_is_rejected()
    {
        // Опечатка в конфиге не должна молча превращаться в keep: иначе теги уедут в текст задачи.
        Assert.Throws<ArgumentException>(() => TagParser.Split("купить хлеб #магазин", "extarct"));
    }

    [Fact]
    public async Task Tags_are_listed_for_many_tasks_at_once()
    {
        string path = Path.Combine(_directory, "kouki.sqlite");
        SqliteTaskStore tasks = new(new SqliteSession(path));
        SqliteTagStore tags = new(path);

        TaskRecord first = Kouki.Kagedama.TaskFactory.Create("купить хлеб", TaskSources.Api);
        TaskRecord second = Kouki.Kagedama.TaskFactory.Create("позвонить маме", TaskSources.Api);
        await tasks.AddAsync(first);
        await tasks.AddAsync(second);
        await tags.AddAsync(first.Id, new[] { "магазин", "дом" }, TagSources.Text);
        await tags.AddAsync(second.Id, new[] { "звонки" }, TagSources.Text);

        IReadOnlyDictionary<Guid, IReadOnlyList<string>> listed =
            await tags.ListForTasksAsync(new[] { first.Id, second.Id });

        Assert.Equal(new[] { "магазин", "дом" }, listed[first.Id]);
        Assert.Equal(new[] { "звонки" }, listed[second.Id]);
        Assert.Empty(await tags.ListForTasksAsync(Array.Empty<Guid>()));
    }
}
