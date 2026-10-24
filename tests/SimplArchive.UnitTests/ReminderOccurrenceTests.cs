using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Reminders;

namespace SimplArchive.UnitTests;

// A recurring reminder repeats at the same WALL time in the zone it was set in, not the same UTC instant (#1911).
// Every expectation is a LITERAL instant: a test that computed its expectation with the AddDays/GetUtcOffset the
// code uses could not fail.
public class ReminderOccurrenceTests
{
    private static readonly TimeZoneInfo Zurich =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Zurich");

    [Theory]
    // Daily across the October 2026 fall-back (25 Oct): 09:00 CEST (07:00Z) → 09:00 CET (08:00Z).
    [InlineData("2026-10-24T09:00:00+02:00", ReminderRecurrence.Daily, "2026-10-25T09:00:00+01:00")]
    [InlineData("2026-10-25T09:00:00+01:00", ReminderRecurrence.Daily, "2026-10-26T09:00:00+01:00")]
    // Daily across the March 2027 spring-forward (28 Mar): 09:00 CET (08:00Z) → 09:00 CEST (07:00Z).
    [InlineData("2027-03-27T09:00:00+01:00", ReminderRecurrence.Daily, "2027-03-28T09:00:00+02:00")]
    // Weekly across each change.
    [InlineData("2026-10-21T09:00:00+02:00", ReminderRecurrence.Weekly, "2026-10-28T09:00:00+01:00")]
    [InlineData("2027-03-24T09:00:00+01:00", ReminderRecurrence.Weekly, "2027-03-31T09:00:00+02:00")]
    // Monthly across each change.
    [InlineData("2026-10-15T09:00:00+02:00", ReminderRecurrence.Monthly, "2026-11-15T09:00:00+01:00")]
    [InlineData("2027-03-15T09:00:00+01:00", ReminderRecurrence.Monthly, "2027-04-15T09:00:00+02:00")]
    // A stored instant in UTC (as Postgres hands it back) is read in the zone first: 07:00Z is 09:00 CEST.
    [InlineData("2026-10-24T07:00:00+00:00", ReminderRecurrence.Daily, "2026-10-25T09:00:00+01:00")]
    public void A_zoned_reminder_keeps_its_wall_time_across_a_clock_change(string from, ReminderRecurrence recurrence, string expected)
    {
        var next = ReminderOccurrence.Next(DateTimeOffset.Parse(from), recurrence, Zurich);

        Assert.Equal(DateTimeOffset.Parse(expected), next);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(next, Zurich).DateTime));
    }

    [Theory]
    // Without a zone (a reminder stored before #1911, or set by an old client) nothing changes: plain UTC
    // arithmetic, so the wall time still drifts an hour at a change — the documented fallback.
    [InlineData("2026-10-24T07:00:00+00:00", ReminderRecurrence.Daily, "2026-10-25T07:00:00+00:00")]
    [InlineData("2026-10-21T07:00:00+00:00", ReminderRecurrence.Weekly, "2026-10-28T07:00:00+00:00")]
    [InlineData("2026-10-15T07:00:00+00:00", ReminderRecurrence.Monthly, "2026-11-15T07:00:00+00:00")]
    public void Without_a_zone_the_utc_arithmetic_is_kept(string from, ReminderRecurrence recurrence, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), ReminderOccurrence.Next(DateTimeOffset.Parse(from), recurrence, zone: null));
    }

    [Fact]
    public void A_missed_stretch_is_caught_up_to_the_first_occurrence_after_now_at_the_wall_time()
    {
        // Daily 09:00 Zurich, last due before the fall-back, the sweep down until the afternoon of 27 October.
        var next = ReminderOccurrence.After(
            DateTimeOffset.Parse("2026-10-23T09:00:00+02:00"),
            ReminderRecurrence.Daily,
            Zurich,
            now: DateTimeOffset.Parse("2026-10-27T15:00:00+01:00"));

        Assert.Equal(DateTimeOffset.Parse("2026-10-28T09:00:00+01:00"), next);
    }

    [Fact]
    public void A_one_shot_has_no_next_occurrence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReminderOccurrence.Next(DateTimeOffset.UtcNow, ReminderRecurrence.None, Zurich));
    }
}
