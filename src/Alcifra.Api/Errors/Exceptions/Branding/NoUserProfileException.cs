namespace Alcifra.Api.Errors.Exceptions.Branding;

// A palette preference belongs to a person; a service account has no self to store one on (ADR 1103).
public sealed class NoUserProfileException : BrandingException
{
    public NoUserProfileException()
        : base("NO_USER_PROFILE", StatusCodes.Status409Conflict,
            "Only a signed-in person can choose a palette.")
    {
    }
}
