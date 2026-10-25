using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.PasswordStores;

/// <summary>
/// A sealed document's audience changed, but not by its author (ADR 0975): "Change audience…" re-seals the content the
/// current version carried, which only somebody who can open it under the present audience can honestly do — and the
/// judgement of who that is belongs to the server, not to the client's toolbar.
/// </summary>
/// <remarks>
/// Beside the sealed-content refusal rather than in an area of its own, because the one guard that can see both facts —
/// the mask and the version comment kind — is the shared finalize guard (ADR 0964's <c>PasswordEntryVersions.GuardAsync</c>).
/// It applies to the Sealed DOCUMENT only: a password store's re-seal is a reader's act after a revocation, not the
/// author's.
/// </remarks>
public sealed class AudienceChangeRequiresTheAuthorException : PasswordStoreException
{
    public AudienceChangeRequiresTheAuthorException()
        : base("AUDIENCE_CHANGE_REQUIRES_AUTHOR", StatusCodes.Status403Forbidden,
            "Only the author of this sealed document's current version can change who it is sealed to.")
    {
    }
}
