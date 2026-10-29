using System.Text.Json.Nodes;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Напоминания как команды: создание, список, подтверждение, отсрочка.
/// </summary>
public sealed class ReminderCommandTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "test-zone",
        TimeSpan.FromHours(5),
        "test-zone",
        "test-zone");

    private readonly string _directory;
    private readonly ServiceProvider _provider;
    private readonly Kouki.Mugura.Dispatcher.IRouter _router;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(5)));

    /// <summary>Готовит базу и маршрутизатор.</summary>
    public ReminderCommandTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-reminder-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        string databasePath = Path.Combine(_directory, "kouki.sqlite");

        var services = new ServiceCollection();
        services.AddKagedamaData(databasePath);
        services.AddMugura(typeof(AddReminderHandler).Assembly);
        services.AddShimiContext();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(Zone);

        _provider = services.BuildServiceProvider();
        DatabaseInitializer.Initialize(databasePath);

        var contexts = _provider.GetRequiredService<Kouki.Shimi.Context.IContextRegistry>();
        contexts.SetDefaultContext(TestHost.Context("test"));

        _router = _provider.GetRequiredService<Kouki.Mugura.Dispatcher.IRouter>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _provider.Dispose();
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
    public async Task Reminder_is_created_from_phrase_with_time()
    {
        JsonNode? result = await _router.RouteAsync(
            "add_reminder",
            new JsonObject { ["text"] = "напомни через 20 минут позвонить маме" });

        Assert.True(result!["created"]!.GetValue<bool>());
        Assert.Equal("напомни позвонить маме", result["text"]!.GetValue<string>());
        Assert.Equal("сегодня в 09:20", result["dueText"]!.GetValue<string>());
    }

    [Fact]
    public async Task Phrase_without_time_explains_itself()
    {
        JsonNode? result = await _router.RouteAsync(
            "add_reminder",
            new JsonObject { ["text"] = "напомни позвонить маме" });

        Assert.False(result!["created"]!.GetValue<bool>());
        Assert.Equal("time-not-understood", result["reason"]!.GetValue<string>());
        Assert.Contains("когда", result["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Due_reminder_is_taken_once_and_can_be_postponed()
    {
        _clock.Now = _clock.Now.AddMinutes(30);

        JsonNode? created = await _router.RouteAsync(
            "add_reminder",
            new JsonObject { ["text"] = "проверить почту" });
        // Новое напоминание без времени не создаётся — ставим вручную через хранилище.
        Assert.False(created!["created"]!.GetValue<bool>());

        var store = _provider.GetRequiredService<IReminderStore>();
        ReminderRecord record = await store.AddAsync(
            "проверить почту",
            _clock.GetUtcNow().AddMinutes(-1),
            null,
            ReminderSources.Telegram,
            null);

        JsonNode? first = await _router.RouteAsync("take_due_reminders", new JsonObject { ["limit"] = 5 });
        JsonNode? second = await _router.RouteAsync("take_due_reminders", new JsonObject { ["limit"] = 5 });

        Assert.Equal(1, first!["count"]!.GetValue<int>());
        Assert.Equal(0, second!["count"]!.GetValue<int>());

        JsonNode? snoozed = await _router.RouteAsync(
            "snooze_reminder",
            new JsonObject { ["id"] = record.Id.ToString(), ["minutes"] = 10 });

        Assert.True(snoozed!["snoozed"]!.GetValue<bool>());
        Assert.Equal("сегодня в 09:40", snoozed["dueText"]!.GetValue<string>());
    }

    [Fact]
    public async Task Acknowledge_marks_reminder_done()
    {
        var store = _provider.GetRequiredService<IReminderStore>();
        ReminderRecord record = await store.AddAsync(
            "выпить воды",
            _clock.GetUtcNow().AddMinutes(-1),
            null,
            ReminderSources.Telegram,
            null);

        JsonNode? done = await _router.RouteAsync(
            "complete_reminder",
            new JsonObject { ["id"] = record.Id.ToString() });

        Assert.True(done!["done"]!.GetValue<bool>());
        Assert.Equal(ReminderStates.Acknowledged, done["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_shows_reminder_with_human_term()
    {
        var store = _provider.GetRequiredService<IReminderStore>();
        await store.AddAsync("посмотреть отчёт", _clock.GetUtcNow().AddHours(2), null, ReminderSources.Api, null);

        JsonNode? list = await _router.RouteAsync("list_reminders", new JsonObject { ["openOnly"] = true });

        Assert.Equal(1, list!["count"]!.GetValue<int>());
        Assert.Equal("сегодня в 11:00", list["reminders"]![0]!["dueText"]!.GetValue<string>());
    }

    private sealed class FakeClock : TimeProvider
    {
        public FakeClock(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
