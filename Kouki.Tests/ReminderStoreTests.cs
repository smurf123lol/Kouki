using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;

namespace Kouki.Tests;

/// <summary>
/// Хранилище напоминаний: срок, отправка, повтор, отмена.
/// </summary>
public sealed class ReminderStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteReminderStore _store;

    /// <summary>Готовит временную базу.</summary>
    public ReminderStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-reminders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "kouki.sqlite");
        DatabaseInitializer.Initialize(path);
        _store = new SqliteReminderStore(path);
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
    public async Task Added_reminder_is_pending_and_listed()
    {
        DateTimeOffset due = DateTimeOffset.UtcNow.AddMinutes(10);
        ReminderRecord added = await _store.AddAsync("позвонить маме", due, null, ReminderSources.Telegram, null);

        IReadOnlyList<ReminderRecord> list = await _store.ListAsync(openOnly: true, limit: 10);

        Assert.Single(list);
        Assert.Equal(ReminderStates.Pending, list[0].State);
        Assert.Equal(added.Id, list[0].Id);
        Assert.Equal(due, list[0].DueAt);
    }

    [Fact]
    public async Task Due_is_selected_once_and_marked_sent()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _store.AddAsync("прошлое", now.AddMinutes(-5), null, ReminderSources.Api, null);
        await _store.AddAsync("будущее", now.AddMinutes(30), null, ReminderSources.Api, null);

        IReadOnlyList<ReminderRecord> first = await _store.DueAsync(now, 10);
        IReadOnlyList<ReminderRecord> second = await _store.DueAsync(now, 10);

        Assert.Single(first);
        Assert.Equal("прошлое", first[0].Text);
        Assert.Equal(ReminderStates.Sent, first[0].State);
        Assert.Empty(second);
    }

    [Fact]
    public async Task Acknowledge_closes_the_reminder()
    {
        ReminderRecord added = await _store.AddAsync("выпить воды", DateTimeOffset.UtcNow, null, ReminderSources.Api, null);

        ReminderRecord? done = await _store.AcknowledgeAsync(added.Id);

        Assert.NotNull(done);
        Assert.Equal(ReminderStates.Acknowledged, done.State);
        Assert.NotNull(done.AcknowledgedAt);
        Assert.Empty(await _store.ListAsync(openOnly: true, limit: 10));
    }

    [Fact]
    public async Task Daily_reminder_returns_with_next_term()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReminderRecord added = await _store.AddAsync("таблетки", now.AddMinutes(-1), "daily", ReminderSources.Api, null);

        ReminderRecord? done = await _store.AcknowledgeAsync(added.Id);

        Assert.NotNull(done);
        Assert.Equal(ReminderStates.Pending, done.State);
        Assert.True(done.DueAt > now);
    }

    [Fact]
    public async Task Snooze_moves_the_term_and_reopens_it()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReminderRecord added = await _store.AddAsync("проверить", now.AddMinutes(-1), null, ReminderSources.Api, null);
        await _store.DueAsync(now, 10);

        ReminderRecord? snoozed = await _store.SnoozeAsync(added.Id, TimeSpan.FromMinutes(10), now);

        Assert.NotNull(snoozed);
        Assert.Equal(ReminderStates.Pending, snoozed.State);
        Assert.Equal(now.AddMinutes(10), snoozed.DueAt);
        Assert.Null(snoozed.SentAt);
    }

    [Fact]
    public async Task Cancel_removes_from_open_list()
    {
        ReminderRecord added = await _store.AddAsync("отменить меня", DateTimeOffset.UtcNow.AddHours(1), null, ReminderSources.Api, null);

        ReminderRecord? cancelled = await _store.CancelAsync(added.Id);

        Assert.NotNull(cancelled);
        Assert.Equal(ReminderStates.Cancelled, cancelled.State);
        Assert.Empty(await _store.ListAsync(openOnly: true, limit: 10));
    }

    [Fact]
    public async Task Reminder_is_linked_to_the_task()
    {
        // Внешний ключ task_id ссылается на tasks: карточку надо создать заранее.
        var tasks = new SqliteTaskStore(Path.Combine(_directory, "kouki.sqlite"));
        TaskRecord task = Kouki.Kagedama.TaskFactory.Create("проверить отчёт", TaskSources.Dashboard);
        await tasks.AddAsync(task, CancellationToken.None);

        ReminderRecord added = await _store.AddAsync(
            "по задаче",
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            ReminderSources.Api,
            task.Id);

        IReadOnlyList<ReminderRecord> linked = await _store.ForTaskAsync(task.Id);

        Assert.Single(linked);
        Assert.Equal(task.Id, linked[0].TaskId);

        ReminderRecord separate = await _store.AddAsync(
            "без задачи",
            DateTimeOffset.UtcNow.AddHours(2),
            null,
            ReminderSources.Api,
            null);

        await _store.LinkTaskAsync(separate.Id, task.Id);
        Assert.Equal(2, (await _store.ForTaskAsync(task.Id)).Count);
    }
}
