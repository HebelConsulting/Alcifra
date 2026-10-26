using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Audience;

/// <summary>
/// A tenant administrator's one audience act (ADR 0983): appointing a manager to an item left without any. Anything else
/// is a manager's.
/// </summary>
public sealed class AdministratorMayOnlyAppointException : ContentAudienceException
{
    public AdministratorMayOnlyAppointException()
        : base("AUDIENCE_ADMINISTRATOR_MAY_ONLY_APPOINT", StatusCodes.Status403Forbidden,
            "With no audience manager left, an administrator may only appoint a manager from among the readers.")
    {
    }
}
