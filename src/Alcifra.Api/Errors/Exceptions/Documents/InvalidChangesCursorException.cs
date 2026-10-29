namespace Alcifra.Api.Errors.Exceptions.Documents;

/// <summary>
/// "Changes since" (ADR 0925) was given a cursor it did not issue. Refused rather than treated as "start now": a
/// client whose cursor broke would otherwise skip every change in between, silently, and keep a stale copy.
/// </summary>
public sealed class InvalidChangesCursorException : DocumentException
{
    public InvalidChangesCursorException()
        : base("INVALID_CHANGES_CURSOR", StatusCodes.Status400BadRequest,
            "The 'since' cursor was not issued by this endpoint. Start again without it, and re-read what you keep.")
    {
    }
}
