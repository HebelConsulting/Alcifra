namespace Alcifra.Api.Errors.Exceptions.Branding;

// A self-service palette choice names a variant the caller's current brand does not carry (ADR 1103).
public sealed class UnknownPaletteException : BrandingException
{
    public UnknownPaletteException()
        : base("UNKNOWN_PALETTE", StatusCodes.Status422UnprocessableEntity,
            "The current brand does not carry that palette.")
    {
    }
}
