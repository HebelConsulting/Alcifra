using SimplArchive.Domain.Documents;
using SimplArchive.Presentation;

namespace SimplArchive.Infrastructure.Reminders;

/// <summary>
/// When a recurring reminder is due next (ADR 0972, #1911): the same WALL time in the zone it was set in, the next
/// day, week or month.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the UTC instant plus a day.</b> <c>RemindAt</c> is an instant. A daily 09:00 reminder set in
/// Zurich in summer is 07:00Z; adding a day to that instant gives 07:00Z on the day after the October change,
/// which is 08:00 there, and it stays an hour early until March. The user asked for "09:00 every day", a wall
/// time, so the arithmetic is done on the wall date and the result is placed back with the offset the zone has
/// at THAT wall time (<see cref="WallClock.At"/> — the placement the clients use when the reminder is typed).
/// </para>
/// <para>
/// <b>No zone, no change.</b> A reminder stored before #1911, or created by a client that does not send its
/// zone, has none; it keeps the old UTC arithmetic rather than having a zone guessed for it — the server's own
/// zone is an accident of deployment (<see cref="DisplayZone"/>'s rule).
/// </para>
/// <para>
/// A monthly reminder on the 31st lands on the 30th or 28th of a shorter month and stays there, exactly as
/// <see cref="DateTime.AddMonths"/> did before; that is not what this change is about.
/// </para>
/// </remarks>
public static class ReminderOccurrence
{
    /// <summary>The first occurrence strictly after <paramref name="now"/> — a sweep that was down for several
    /// periods fires once and schedules the next future one, rather than once per missed period.</summary>
    public static DateTimeOffset After(DateTimeOffset from, ReminderRecurrence recurrence, TimeZoneInfo? zone, DateTimeOffset now)
    {
        var next = Next(from, recurrence, zone);
        while (next <= now)
        {
            next = Next(next, recurrence, zone);
        }

        return next;
    }

    /// <summary>The occurrence one period after <paramref name="value"/>; in <paramref name="zone"/> when there is one.</summary>
    public static DateTimeOffset Next(DateTimeOffset value, ReminderRecurrence recurrence, TimeZoneInfo? zone)
    {
        if (zone is null)
        {
            return recurrence switch
            {
                ReminderRecurrence.Daily => value.AddDays(1),
                ReminderRecurrence.Weekly => value.AddDays(7),
                ReminderRecurrence.Monthly => value.AddMonths(1),
                _ => throw NotRecurring(recurrence),
            };
        }

        var wall = value.InZone(zone).DateTime;
        var date = DateOnly.FromDateTime(wall);
        var nextDate = recurrence switch
        {
            ReminderRecurrence.Daily => date.AddDays(1),
            ReminderRecurrence.Weekly => date.AddDays(7),
            ReminderRecurrence.Monthly => date.AddMonths(1),
            _ => throw NotRecurring(recurrence),
        };

        return WallClock.At(nextDate, TimeOnly.FromDateTime(wall), zone);
    }

    private static ArgumentOutOfRangeException NotRecurring(ReminderRecurrence recurrence) =>
        new(nameof(recurrence), recurrence, "Not a recurring cadence.");
}
