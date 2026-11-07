using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Documents;

// A document's mask may not change while a folder or a reference field holds it and would not take the new one (#2214,
// ADR 1074). 409 and its own code, because the remedy is to take the document out of what holds it, not another value.
public sealed class MaskHeldByReferenceException : DocumentException
{
    public MaskHeldByReferenceException(string message)
        : base("MASK_HELD_BY_REFERENCE", StatusCodes.Status409Conflict, message)
    {
    }
}
