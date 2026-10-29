using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Reminders;

// Thrown when a reminder names a time zone this server does not know (ADR 0972, #1911). Refused rather than stored
// or ignored: the zone is what keeps a recurring reminder at its wall time, and one the sweep cannot resolve would
// quietly fall back to UTC and drift at the next clock change — the defect the field exists to prevent. A client
// that does not know its zone omits the field instead.
public sealed class UnknownReminderTimeZoneException : ReminderException
{
    public UnknownReminderTimeZoneException(string timeZoneId)
        : base("UNKNOWN_REMINDER_TIME_ZONE", StatusCodes.Status400BadRequest,
            $"'{timeZoneId}' is not a time zone this server knows. Send an IANA zone id, or omit the time zone.")
    {
    }
}
