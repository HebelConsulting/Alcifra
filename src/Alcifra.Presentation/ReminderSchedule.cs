namespace Alcifra.Presentation;

/// <summary>
/// When a new reminder is preset to ("tomorrow, 09:00"), and how the day and time a user typed become the instant
/// the server stores — one answer for the web, the desktop and the phone (ADR 0650's rule, #1909).
/// </summary>
/// <remarks>
/// <para>
/// <b>Tomorrow is a calendar day, not 24 hours.</b> The day before a clock change, tomorrow has 25 hours (last
/// Sunday of October in Europe) or 23 (last Sunday of March), so "now plus a day" and "today's offset" are both
/// wrong then. The default is the user's local DATE plus one, and the instant is placed with the offset the zone
/// has on that date at that wall time (<see cref="WallClock.At"/>).
/// </para>
/// <para>
/// <b>Why this exists</b> (#1909): the web dialog defaulted to <c>DateTime.Today.AddDays(1)</c> and stored
/// <c>new DateTimeOffset(day + time, DateTimeOffset.Now.Offset)</c> — TODAY's offset. On 24 October 2026 that is
/// +02:00 for a wall time whose offset is +01:00, and because <c>DateTime.Today</c> carries
/// <see cref="DateTimeKind.Local"/>, the constructor threw: Set reminder did nothing all day before each change
/// (the snackbar said it could not set one). For a date the user picked it did not throw, and stored the reminder
/// an hour off instead.
/// </para>
/// <para>
/// <b>The zone is the user's display zone</b> (<see cref="DisplayZone"/>), the one the reminder list shows the
/// stored instant in: typing in one zone and reading back in another shows a time nobody typed. It is a
/// parameter, never <see cref="TimeZoneInfo.Local"/> read in here, so each caller names it.
/// </para>
/// </remarks>
public static class ReminderSchedule
{
    /// <summary>The time of day a new reminder is preset to.</summary>
    public static TimeOnly DefaultTime { get; } = new(9, 0);

    /// <summary>Tomorrow in <paramref name="zone"/>: the calendar day after the one <paramref name="now"/> falls on there.</summary>
    /// <param name="now">The current instant, from the caller's clock (a <see cref="TimeProvider"/>).</param>
    /// <param name="zone">The user's display zone.</param>
    public static DateOnly DefaultDate(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(now.InZone(zone).DateTime).AddDays(1);
}
