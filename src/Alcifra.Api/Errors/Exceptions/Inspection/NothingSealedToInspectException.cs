using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Inspection;

// The document's content is sealed to no audience, or it has no content yet: there is no envelope to describe.
public sealed class NothingSealedToInspectException : SealInspectionException
{
    public NothingSealedToInspectException()
        : base("NOTHING_SEALED_TO_INSPECT", StatusCodes.Status404NotFound,
            "This item holds no sealed content to inspect.")
    {
    }
}
