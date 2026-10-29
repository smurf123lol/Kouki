using System.Globalization;
using System.Text.RegularExpressions;

namespace Kouki.Kagedama;

/// <summary>
/// Разобранное напоминание: срок, повтор и оставшийся текст.
/// </summary>
/// <param name="DueAt">Когда напомнить (UTC).</param>
/// <param name="Repeat">Повтор: <c>null</c>, <c>daily</c>, <c>weekly</c>.</param>
/// <param name="Text">О чём напомнить (фраза без выражения времени).</param>
public sealed record ParsedReminder(DateTimeOffset DueAt, string? Repeat, string Text);

/// <summary>
/// Разбор времени напоминания из русской фразы.
/// </summary>
/// <remarks>
/// Время считается от момента произнесения и в часовом поясе человека, а не сервера:
/// «завтра в 9» на сервере в UTC означало бы 9:00 UTC, то есть 14:00 по Екатеринбургу.
/// Поэтому наружу отдаётся уже абсолютный момент, а пояс передаётся снаружи.
/// </remarks>
public static class ReminderTime
{
    /// <summary>Время по умолчанию, если сказано «завтра» без часа.</summary>
    public static readonly TimeSpan DefaultDayTime = new(9, 0, 0);

    private static readonly Regex Relative = new(
        @"(?:^|\s)через\s+(?<count>\d+|пол(?:овину)?)\s*(?<unit>минут[уы]?|мин\.?|час[а]?|ч\.?|день|дня|дней|недел[юи]|недель)(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DayShift = new(
        @"(?:^|\s)(?<word>завтра|послезавтра)(?:\s+в\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?)?(?=\s|$|[,.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TodayAt = new(
        @"(?:^|\s)сегодня\s+в\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?=\s|$|[,.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Weekday = new(
        @"(?:^|\s)в\s+(?<day>понедельник|вторник|среду|четверг|пятницу|субботу|воскресенье)(?:\s+в\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?)?(?=\s|$|[,.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Daily = new(
        @"(?:^|\s)(?<prefix>каждый\s+день|ежедневно)(?:\s+в\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?)?(?=\s|$|[,.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Weekly = new(
        @"(?:^|\s)(?<prefix>каждую\s+неделю|еженедельно|каждый\s+(?<day>понедельник|вторник|среду|четверг|пятницу|субботу|воскресенье))" +
        @"(?:\s+в\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?)?(?=\s|$|[,.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitHour = new(
        @"(?:^|\s)в\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?=\s|$|[,.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IsoDate = new(
        @"(?:^|\s)(?<iso>\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2})?)(?=\s|$|[,.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Пытается разобрать фразу напоминания.
    /// </summary>
    /// <param name="phrase">Фраза целиком («напомни через 20 минут позвонить маме»).</param>
    /// <param name="now">Текущий момент.</param>
    /// <param name="zone">Часовой пояс человека.</param>
    /// <param name="parsed">Результат разбора.</param>
    public static bool TryParse(string? phrase, DateTimeOffset now, TimeZoneInfo zone, out ParsedReminder parsed)
    {
        parsed = null!;

        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, zone);
        string text = phrase.Trim();

        if (TryRelative(text, local, out DateTimeOffset due, out string rest))
        {
            return Finish(due, null, rest, out parsed);
        }

        if (Daily.Match(text) is { Success: true } daily)
        {
            (int hour, int minute) = TimeOfDay(daily, DefaultDayTime);
            DateTimeOffset moment = NextOccurrence(local, local.Date.AddHours(hour).AddMinutes(minute));
            return Finish(moment, "daily", Cut(text, daily), out parsed);
        }

        if (Weekly.Match(text) is { Success: true } weekly)
        {
            (int hour, int minute) = TimeOfDay(weekly, DefaultDayTime);
            DayOfWeek? weekday = weekly.Groups["day"].Success ? ToWeekday(weekly.Groups["day"].Value) : null;
            DateTime target = local.Date.AddHours(hour).AddMinutes(minute);
            if (weekday is { } day)
            {
                target = NextWeekday(local.Date, day).AddHours(hour).AddMinutes(minute);
            }

            DateTimeOffset moment = NextOccurrence(local, target);
            return Finish(moment, "weekly", Cut(text, weekly), out parsed);
        }

        if (DayShift.Match(text) is { Success: true } shift)
        {
            int days = shift.Groups["word"].Value.StartsWith("послезавтра", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            (int hour, int minute) = TimeOfDay(shift, DefaultDayTime);
            DateTimeOffset moment = AtZone(local, local.Date.AddDays(days).AddHours(hour).AddMinutes(minute));
            return Finish(moment, null, Cut(text, shift), out parsed);
        }

        if (TodayAt.Match(text) is { Success: true } today)
        {
            (int hour, int minute) = TimeOfDay(today, DefaultDayTime);
            DateTimeOffset moment = AtZone(local, local.Date.AddHours(hour).AddMinutes(minute));
            if (moment <= local)
            {
                moment = moment.AddDays(1);
            }

            return Finish(moment, null, Cut(text, today), out parsed);
        }

        if (Weekday.Match(text) is { Success: true } weekdayMatch)
        {
            (int hour, int minute) = TimeOfDay(weekdayMatch, DefaultDayTime);
            DayOfWeek day = ToWeekday(weekdayMatch.Groups["day"].Value);
            DateTimeOffset moment = AtZone(local, NextWeekday(local.Date, day).AddHours(hour).AddMinutes(minute));
            if (moment <= local)
            {
                moment = moment.AddDays(7);
            }

            return Finish(moment, null, Cut(text, weekdayMatch), out parsed);
        }

        if (IsoDate.Match(text) is { Success: true } iso)
        {
            string value = iso.Groups["iso"].Value.Replace(' ', 'T');
            string format = value.Length == 10 ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm";
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact))
            {
                return Finish(AtZone(local, exact), null, Cut(text, iso), out parsed);
            }
        }

        if (ExplicitHour.Match(text) is { Success: true } clock)
        {
            (int hour, int minute) = TimeOfDay(clock, DefaultDayTime);
            DateTimeOffset moment = AtZone(local, local.Date.AddHours(hour).AddMinutes(minute));
            if (moment <= local)
            {
                moment = moment.AddDays(1);
            }

            return Finish(moment, null, Cut(text, clock), out parsed);
        }

        return false;
    }

    /// <summary>
    /// Человеческое описание срока для ответа человеку («завтра в 09:00»).
    /// </summary>
    /// <param name="dueAt">Срок.</param>
    /// <param name="now">Текущий момент.</param>
    /// <param name="zone">Часовой пояс.</param>
    /// <param name="repeat">Повтор.</param>
    public static string Describe(DateTimeOffset dueAt, DateTimeOffset now, TimeZoneInfo zone, string? repeat)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(dueAt, zone);
        DateTimeOffset nowLocal = TimeZoneInfo.ConvertTime(now, zone);
        string clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (string.Equals(repeat, "daily", StringComparison.Ordinal))
        {
            return $"каждый день в {clock}";
        }

        if (string.Equals(repeat, "weekly", StringComparison.Ordinal))
        {
            return $"каждую неделю, {Word(local.DayOfWeek)}, в {clock}";
        }

        int days = (local.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => $"сегодня в {clock}",
            1 => $"завтра в {clock}",
            2 => $"послезавтра в {clock}",
            _ => $"{local:dd.MM} в {clock}",
        };
    }

    private static bool TryRelative(string text, DateTimeOffset local, out DateTimeOffset due, out string rest)
    {
        due = default;
        rest = text;

        Match match = Relative.Match(text);
        if (!match.Success)
        {
            return false;
        }

        string countText = match.Groups["count"].Value.ToLowerInvariant();
        string unit = match.Groups["unit"].Value.ToLowerInvariant();

        double count = countText.StartsWith("пол", StringComparison.Ordinal) ? 0.5 : double.Parse(countText, CultureInfo.InvariantCulture);

        TimeSpan step;
        if (unit.StartsWith("минут", StringComparison.Ordinal) || unit.StartsWith("мин", StringComparison.Ordinal))
        {
            step = TimeSpan.FromMinutes(count);
        }
        else if (unit.StartsWith("час", StringComparison.Ordinal) || unit == "ч" || unit == "ч.")
        {
            step = TimeSpan.FromHours(count);
        }
        else if (unit.StartsWith("недел", StringComparison.Ordinal))
        {
            step = TimeSpan.FromDays(7 * count);
        }
        else
        {
            step = TimeSpan.FromDays(count);
        }

        if (step <= TimeSpan.Zero)
        {
            return false;
        }

        due = local.Add(step);
        rest = Cut(text, match);
        return true;
    }

    private static bool Finish(DateTimeOffset due, string? repeat, string rest, out ParsedReminder parsed)
    {
        string cleaned = Clean(rest);
        if (cleaned.Length == 0)
        {
            parsed = null!;
            return false;
        }

        parsed = new ParsedReminder(due.ToUniversalTime(), repeat, cleaned);
        return true;
    }

    private static (int Hour, int Minute) TimeOfDay(Match match, TimeSpan fallback)
    {
        if (!match.Groups["hour"].Success)
        {
            return (fallback.Hours, fallback.Minutes);
        }

        int hour = Math.Clamp(int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture), 0, 23);
        int minute = match.Groups["minute"].Success
            ? Math.Clamp(int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture), 0, 59)
            : 0;

        return (hour, minute);
    }

    private static DateTimeOffset AtZone(DateTimeOffset local, DateTime value) =>
        new(value, local.Offset);

    private static DateTimeOffset NextOccurrence(DateTimeOffset local, DateTime target)
    {
        DateTimeOffset moment = AtZone(local, target);
        return moment <= local ? AtZone(local, target.AddDays(1)) : moment;
    }

    private static DateTime NextWeekday(DateTime from, DayOfWeek day)
    {
        int delta = ((int)day - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(delta);
    }

    private static DayOfWeek ToWeekday(string value) => value.ToLowerInvariant() switch
    {
        "понедельник" => DayOfWeek.Monday,
        "вторник" => DayOfWeek.Tuesday,
        "среду" => DayOfWeek.Wednesday,
        "четверг" => DayOfWeek.Thursday,
        "пятницу" => DayOfWeek.Friday,
        "субботу" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    private static string Word(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "понедельник",
        DayOfWeek.Tuesday => "вторник",
        DayOfWeek.Wednesday => "среда",
        DayOfWeek.Thursday => "четверг",
        DayOfWeek.Friday => "пятница",
        DayOfWeek.Saturday => "суббота",
        _ => "воскресенье",
    };

    private static string Cut(string text, Match match)
    {
        string before = text[..match.Index];
        string after = text[(match.Index + match.Length)..];
        return Clean(before + " " + after);
    }

    private static string Clean(string text)
    {
        string cleaned = Regex.Replace(text, @"\s{2,}", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"^[,.;:!?\-–—]+\s*", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s*[,.;:!?\-–—]+$", string.Empty);
        return cleaned.Trim();
    }
}
