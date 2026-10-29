using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Documents;

public sealed class InvalidDocumentDateException : DocumentException
{
    public InvalidDocumentDateException(string message)
        : base("INVALID_DOCUMENT_DATE", StatusCodes.Status400BadRequest, message)
    {
    }
}
