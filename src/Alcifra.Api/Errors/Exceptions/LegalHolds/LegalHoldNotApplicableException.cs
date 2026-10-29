using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.LegalHolds;

// Thrown when a document is added to a hold although its mask's module declares it outside legal hold (ABI 1.18,
// ADR 0937): its content is sealed to its readers and its lifetime is the module's.
public sealed class LegalHoldNotApplicableException : LegalHoldException
{
    public LegalHoldNotApplicableException()
        : base("LEGAL_HOLD_NOT_APPLICABLE", StatusCodes.Status409Conflict,
            "This kind of document cannot be placed under a legal hold: the module that files it owns its lifetime.")
    {
    }
}
