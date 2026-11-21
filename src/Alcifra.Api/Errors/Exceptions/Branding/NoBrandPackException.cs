namespace Alcifra.Api.Errors.Exceptions.Branding;

// The tenant wears the installation's own brand, so there is no palette to choose (ADR 1103).
public sealed class NoBrandPackException : BrandingException
{
    public NoBrandPackException()
        : base("NO_BRAND_PACK", StatusCodes.Status409Conflict,
            "This tenant wears the installation's brand; there is no palette to choose.")
    {
    }
}
