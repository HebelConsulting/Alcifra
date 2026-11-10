using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Inspection;

// The envelope is larger than the server reads whole for a description; `alcifra inspect --document` reads it locally.
public sealed class SealTooLargeToInspectException : SealInspectionException
{
    public SealTooLargeToInspectException(long limit)
        : base("SEAL_TOO_LARGE_TO_INSPECT", StatusCodes.Status413PayloadTooLarge,
            $"This content is larger than {limit / (1024 * 1024)} MB, which is inspected here at most; use alcifra inspect --document.")
    {
    }
}
