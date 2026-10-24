namespace SimplArchive.Presentation;

/// <summary>
/// A day and a time of day a user typed, placed in a zone: the instant they name there (#1909).
/// </summary>
/// <remarks>
/// <para>
/// <b>The offset is the zone's offset at THAT wall time</b> — not today's (the reminder dialog's defect, #1909),
/// and not the date's midnight (the booking dialogs': on the day the clocks change, midnight and 09:00 have
/// different offsets).
/// </para>
/// <para>
/// A wall time inside a spring-forward gap (which does not exist) gets the offset before the gap, so it lands on
/// the first real time after it, as <see cref="DocumentDateFormat.ToUtc"/> reads a typed time; one inside the
/// repeated hour of a fall-back is read in standard time, a real occurrence of the time typed. A
/// <see cref="DateOnly"/> carries no <see cref="DateTimeKind"/>, so a picker's Local-kind value cannot make the
/// construction throw — <c>new DateTimeOffset(localKind, offset)</c> throws whenever the offset is not the
/// machine's own at that time, which is what silenced the web's Set reminder button the day before each change.
/// </para>
/// </remarks>
public static class WallClock
{
    /// <summary>The instant <paramref name="time"/> on <paramref name="date"/> names in <paramref name="zone"/>.</summary>
    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var wall = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}
