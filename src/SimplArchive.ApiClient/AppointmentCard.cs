using System.Globalization;
using System.Text.Json;

namespace SimplArchive.ApiClient;

/// <summary>
/// An appointment as the API's <c>appointment</c> resource carries it, without any UI: what the desktop's appointment
/// form and the phone's Calendar (SimplArchiveMobile #47) both read and save (core ADR 0917: moved, not copied).
/// </summary>
/// <remarks>
/// <para><b>Times are wall clocks</b>, sent without an offset: the zone travels separately in the per-endpoint zone
/// ids, and attaching an offset is how a floating time stops floating (ADR 0631).</para>
/// <para><b>The recurrence rule is sent back unchanged</b> unless the caller changed it: the merge clears a rule handed
/// to it as null, so omitting it would silently un-repeat every recurring appointment anyone opened (#1133).</para>
/// </remarks>
public sealed record AppointmentCard(
    string Summary, string Location, string Description, bool IsAllDay,
    DateTime? Start, DateTime? End, string StartTimeZoneId, string EndTimeZoneId, string Url,
    string? RecurrenceRule, int ReminderCount, IReadOnlyList<AppointmentCard.Attendee> Attendees)
{
    /// <summary>Someone invited, as the server read them (shown, not edited).</summary>
    public sealed record Attendee(string Name, string Address, string Status);

    /// <summary>A new entry on the next full hour, running an hour, stamped with <paramref name="zoneId"/> (#631, #1126).</summary>
    public static AppointmentCard NewAt(DateTime now, string zoneId)
    {
        var start = DateTime.SpecifyKind(now.Date.AddHours(now.Hour + 1), DateTimeKind.Unspecified);
        return new AppointmentCard(string.Empty, string.Empty, string.Empty, false, start, start.AddHours(1), zoneId, zoneId, string.Empty, null, 0, []);
    }

    /// <summary>Reads the API's appointment resource.</summary>
    public static AppointmentCard From(JsonElement body) => new(
        Text(body, "summary"), Text(body, "location"), Text(body, "description"),
        body.TryGetProperty("isAllDay", out var allDay) && allDay.ValueKind == JsonValueKind.True,
        Parse(Text(body, "start")), Parse(Text(body, "end")),
        // The per-endpoint zones, falling back to the single one a server predating them would send.
        First(Text(body, "startTimeZoneId"), Text(body, "timeZoneId")),
        First(Text(body, "endTimeZoneId"), Text(body, "startTimeZoneId"), Text(body, "timeZoneId")),
        Text(body, "url"),
        Text(body, "recurrenceRule") is { Length: > 0 } rule ? rule : null,
        body.TryGetProperty("reminderCount", out var count) && count.TryGetInt32(out var n) ? n : 0,
        body.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array
            ? [.. attendees.EnumerateArray().Select(a => new Attendee(Text(a, "name"), Text(a, "address"), Text(a, "status")))]
            : []);

    /// <summary>The body a PUT (or a create) sends; <paramref name="scope"/> and <paramref name="recurrenceId"/> say
    /// which occurrences of a repeating entry an edit is about (#1133), null for the whole series or a single entry.</summary>
    public object ToPayload(string? scope = null, string? recurrenceId = null) => new
    {
        scope,
        recurrenceId,
        summary = Null(Summary),
        start = Wall(Start),
        end = Wall(End),
        isAllDay = IsAllDay,
        startTimeZoneId = Null(StartTimeZoneId),
        endTimeZoneId = Null(EndTimeZoneId),
        location = Null(Location),
        description = Null(Description),
        url = Null(Url),
        recurrenceRule = RecurrenceRule,
    };

    /// <summary>The wall clock serialized WITHOUT an offset.</summary>
    public static string? Wall(DateTime? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static string First(params string[] candidates) => Array.Find(candidates, c => !string.IsNullOrEmpty(c)) ?? string.Empty;

    private static DateTime? Parse(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
