namespace Alcifra.Api.Errors.Exceptions.Branding;

// An assignment names a brand pack the installation does not carry (ADR 1103). The available ids ride the Problem
// Details response as data, so an operator's tool can list them without reading prose.
public sealed class UnknownBrandPackException : BrandingException
{
    public UnknownBrandPackException(IReadOnlyList<string> availablePackIds)
        : base("UNKNOWN_BRAND_PACK", StatusCodes.Status422UnprocessableEntity,
            availablePackIds.Count == 0
                ? "This installation carries no brand packs."
                : "This installation carries: " + string.Join(", ", availablePackIds) + ".",
            new Dictionary<string, object?> { ["availablePackIds"] = availablePackIds })
    {
    }
}
