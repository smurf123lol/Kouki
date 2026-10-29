using Kouki.Kagedama;

namespace Kouki.Tests;

/// <summary>
/// Разбор времени напоминания: «через 20 минут», «завтра в 9», «каждый день в 8».
/// </summary>
public sealed class ReminderTimeTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "test-zone",
        TimeSpan.FromHours(5),
        "test-zone",
        "test-zone");

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(5));

    [Fact]
    public void Relative_minutes_move_the_moment_forward()
    {
        Assert.True(ReminderTime.TryParse("напомни через 20 минут позвонить маме", Now, Zone, out ParsedReminder parsed));

        Assert.Equal(Now.AddMinutes(20).ToUniversalTime(), parsed.DueAt);
        Assert.Equal("напомни позвонить маме", parsed.Text);
        Assert.Null(parsed.Repeat);
    }

    [Fact]
    public void Relative_hours_and_days_are_supported()
    {
        Assert.True(ReminderTime.TryParse("через 2 часа чай", Now, Zone, out ParsedReminder hours));
        Assert.Equal(Now.AddHours(2).ToUniversalTime(), hours.DueAt);

        Assert.True(ReminderTime.TryParse("через 3 дня отчёт", Now, Zone, out ParsedReminder days));
        Assert.Equal(Now.AddDays(3).ToUniversalTime(), days.DueAt);
    }

    [Fact]
    public void Half_an_hour_is_understood()
    {
        Assert.True(ReminderTime.TryParse("через полчаса выключить плиту", Now, Zone, out ParsedReminder parsed));
        Assert.Equal(Now.AddMinutes(30).ToUniversalTime(), parsed.DueAt);
    }

    [Fact]
    public void Tomorrow_without_hour_defaults_to_nine()
    {
        Assert.True(ReminderTime.TryParse("завтра забрать посылку", Now, Zone, out ParsedReminder parsed));

        DateTimeOffset local = TimeZoneInfo.ConvertTime(parsed.DueAt, Zone);
        Assert.Equal(31, local.Day);
        Assert.Equal(9, local.Hour);
    }

    [Fact]
    public void Tomorrow_with_hour_uses_it()
    {
        Assert.True(ReminderTime.TryParse("завтра в 18:30 встреча", Now, Zone, out ParsedReminder parsed));

        DateTimeOffset local = TimeZoneInfo.ConvertTime(parsed.DueAt, Zone);
        Assert.Equal(18, local.Hour);
        Assert.Equal(30, local.Minute);
    }

    [Fact]
    public void Bare_hour_in_the_past_moves_to_tomorrow()
    {
        Assert.True(ReminderTime.TryParse("в 8 утра зарядка", Now, Zone, out ParsedReminder parsed));

        DateTimeOffset local = TimeZoneInfo.ConvertTime(parsed.DueAt, Zone);
        Assert.Equal(31, local.Day);
        Assert.Equal(8, local.Hour);
    }

    [Fact]
    public void Weekly_repeat_keeps_the_weekday()
    {
        Assert.True(ReminderTime.TryParse("каждый день в 8:00 таблетки", Now, Zone, out ParsedReminder parsed));
        Assert.Equal("daily", parsed.Repeat);
        Assert.Equal("таблетки", parsed.Text);

        DateTimeOffset local = TimeZoneInfo.ConvertTime(parsed.DueAt, Zone);
        Assert.Equal(8, local.Hour);
    }

    [Fact]
    public void Weekday_is_next_occurrence()
    {
        // 30.10.2026 — пятница; «в среду» значит 04.11.
        Assert.True(ReminderTime.TryParse("в среду в 10:00 планёрка", Now, Zone, out ParsedReminder parsed));

        DateTimeOffset local = TimeZoneInfo.ConvertTime(parsed.DueAt, Zone);
        Assert.Equal(DayOfWeek.Wednesday, local.DayOfWeek);
        Assert.Equal(4, local.Day);
    }

    [Fact]
    public void Phrase_without_time_is_rejected()
    {
        Assert.False(ReminderTime.TryParse("напомни позвонить маме", Now, Zone, out _));
    }

    [Fact]
    public void Time_without_reminder_text_is_rejected()
    {
        Assert.False(ReminderTime.TryParse("через 20 минут", Now, Zone, out _));
    }

    [Fact]
    public void Describe_reads_naturally()
    {
        Assert.Equal(
            "завтра в 09:00",
            ReminderTime.Describe(Now.AddDays(1).ToUniversalTime(), Now, Zone, null));

        Assert.Equal(
            "каждый день в 09:00",
            ReminderTime.Describe(Now.AddDays(1).ToUniversalTime(), Now, Zone, "daily"));
    }

    [Fact]
    public void Timezone_matters_for_the_absolute_moment()
    {
        Assert.True(ReminderTime.TryParse("завтра в 9:00 созвон", Now, Zone, out ParsedReminder parsed));

        // 9:00 по поясу +05:00 — это 04:00 UTC, а не 09:00 UTC.
        Assert.Equal(4, parsed.DueAt.UtcDateTime.Hour);
    }
}
