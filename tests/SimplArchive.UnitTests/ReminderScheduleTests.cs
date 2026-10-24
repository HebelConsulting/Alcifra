using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The reminder dialog's default ("tomorrow, 09:00") and the turn of the typed day and time into the instant the
// server stores, shared by the web, the desktop and the phone (#1909). Every expectation is a LITERAL instant: a
// test that computed its expectation with the same AddDays/GetUtcOffset the code uses could not fail.
public class ReminderScheduleTests
{
    private static readonly TimeZoneInfo Zurich =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Zurich");

    private static readonly TimeZoneInfo NewYork =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    // "now" as a UTC instant, the way a TimeProvider hands it out; the zone decides which calendar day that is.
    [Theory]
    // An ordinary Saturday in September (+02:00 both days).
    [InlineData("2026-09-12T10:00:00+00:00", "2026-09-13", "2026-09-13T09:00:00+02:00")]
    // The day before the clocks go back: tomorrow, 25 October 2026, has 25 hours and its 09:00 is CET (+01:00).
    // The web dialog built exactly this instant with TODAY's offset (+02:00) and threw before it sent anything.
    [InlineData("2026-10-24T10:00:00+00:00", "2026-10-25", "2026-10-25T09:00:00+01:00")]
    // Late on that day before: 23:30 in Zurich is already the 24th's last half hour, still "tomorrow" = the 25th.
    [InlineData("2026-10-24T21:30:00+00:00", "2026-10-25", "2026-10-25T09:00:00+01:00")]
    // ON the fall-back day, after the change: tomorrow is Monday the 26th.
    [InlineData("2026-10-25T10:00:00+00:00", "2026-10-26", "2026-10-26T09:00:00+01:00")]
    // 00:30 on the 25-hour day itself (22:30 UTC the evening before): now+24h is 23:30 the SAME day — the
    // stopwatch says "today", the calendar says Monday the 26th.
    [InlineData("2026-10-24T22:30:00+00:00", "2026-10-26", "2026-10-26T09:00:00+01:00")]
    // The day before the clocks go forward: tomorrow, 28 March 2027, has 23 hours and its 09:00 is CEST (+02:00).
    [InlineData("2027-03-27T10:00:00+00:00", "2027-03-28", "2027-03-28T09:00:00+02:00")]
    // ON the spring-forward day: tomorrow is Monday the 29th.
    [InlineData("2027-03-28T10:00:00+00:00", "2027-03-29", "2027-03-29T09:00:00+02:00")]
    // 23:30 the evening before the 23-hour day (22:30 UTC): now+24h is 00:30 on the 29th — the stopwatch skips
    // "tomorrow" altogether, the calendar says the 28th.
    [InlineData("2027-03-27T22:30:00+00:00", "2027-03-28", "2027-03-28T09:00:00+02:00")]
    public void Tomorrow_is_the_next_calendar_day_in_the_users_zone(string nowUtc, string tomorrow, string defaultInstant)
    {
        var now = DateTimeOffset.Parse(nowUtc);

        var date = ReminderSchedule.DefaultDate(now, Zurich);

        Assert.Equal(DateOnly.Parse(tomorrow), date);
        Assert.Equal(DateTimeOffset.Parse(defaultInstant), WallClock.At(date, ReminderSchedule.DefaultTime, Zurich));
        Assert.Equal(TimeSpan.Parse(defaultInstant[20..]), WallClock.At(date, ReminderSchedule.DefaultTime, Zurich).Offset);
    }

    [Fact]
    public void The_users_zone_decides_the_day_not_the_utc_date()
    {
        // 02:00 UTC on 24 October is still the 23rd's evening in New York: tomorrow there is the 24th.
        var now = DateTimeOffset.Parse("2026-10-24T02:00:00+00:00");

        Assert.Equal(new DateOnly(2026, 10, 24), ReminderSchedule.DefaultDate(now, NewYork));
        Assert.Equal(new DateOnly(2026, 10, 25), ReminderSchedule.DefaultDate(now, Zurich));
    }

    [Fact]
    public void A_non_european_zone_changes_on_its_own_dates()
    {
        // New York falls back on 1 November 2026, a week after Europe: on 24 October both days are EDT (-04:00) …
        var beforeEurope = ReminderSchedule.DefaultDate(DateTimeOffset.Parse("2026-10-24T14:00:00+00:00"), NewYork);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T09:00:00-04:00"), WallClock.At(beforeEurope, ReminderSchedule.DefaultTime, NewYork));

        // … and the day before its own change, tomorrow's 09:00 is EST (-05:00).
        var beforeOwn = ReminderSchedule.DefaultDate(DateTimeOffset.Parse("2026-10-31T14:00:00+00:00"), NewYork);
        Assert.Equal(new DateOnly(2026, 11, 1), beforeOwn);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T09:00:00-05:00"), WallClock.At(beforeOwn, ReminderSchedule.DefaultTime, NewYork));
    }

    [Fact]
    public void On_the_change_day_itself_a_time_takes_its_own_offset_not_midnights()
    {
        // The booking dialogs placed both ends with the DATE's offset (midnight's). Midnight on 25 October 2026 is
        // still CEST (+02:00); 09:00 that day is CET (+01:00), so a 09:00 booking landed at 08:00.
        Assert.Equal(
            DateTimeOffset.Parse("2026-10-25T09:00:00+01:00"),
            WallClock.At(new DateOnly(2026, 10, 25), new TimeOnly(9, 0), Zurich));
        Assert.Equal(
            DateTimeOffset.Parse("2027-03-28T09:00:00+02:00"),
            WallClock.At(new DateOnly(2027, 3, 28), new TimeOnly(9, 0), Zurich));
    }

    [Fact]
    public void A_time_typed_into_the_skipped_hour_is_the_first_real_one_after_it()
    {
        // 02:30 on 28 March 2027 does not exist in Zurich (02:00 jumps to 03:00): it is read with the offset before
        // the gap, which lands on 03:30 CEST — the same reading DocumentDateFormat.ToUtc gives a typed time.
        Assert.Equal(
            DateTimeOffset.Parse("2027-03-28T01:30:00+00:00"),
            WallClock.At(new DateOnly(2027, 3, 28), new TimeOnly(2, 30), Zurich));
    }

    [Fact]
    public void A_time_typed_into_the_repeated_hour_is_a_real_instant_in_it()
    {
        // 02:30 on 25 October 2026 happens twice in Zurich; either reading is honest, and it must not throw.
        var instant = WallClock.At(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), Zurich);

        Assert.Contains(instant, new[] { DateTimeOffset.Parse("2026-10-25T02:30:00+02:00"), DateTimeOffset.Parse("2026-10-25T02:30:00+01:00") });
        Assert.Equal(new TimeOnly(2, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zurich).DateTime));
    }

    [Fact]
    public void A_date_that_still_carries_a_local_kind_does_not_throw()
    {
        // The web dialog's default was DateTime.Today.AddDays(1) — Kind=Local — and `new DateTimeOffset(local, x)`
        // throws whenever x is not the MACHINE's offset at that wall time. The rule takes a DateOnly, so the kind
        // cannot reach it; the clients' DateTime → DateOnly hop is what this pins.
        var picked = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Local);

        var instant = WallClock.At(DateOnly.FromDateTime(picked), new TimeOnly(9, 0), NewYork);

        Assert.Equal(DateTimeOffset.Parse("2026-10-25T09:00:00-04:00"), instant);
    }
}
