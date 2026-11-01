using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Concurrency;

// Thrown when a mutation's If-Match ETag is stale — the resource changed since it was read (ADR "ETag / If-Match
// concurrency"). The factories preserve each resource's message; all share the ETAG_MISMATCH wire code.
public sealed class EtagMismatchException : ConcurrencyException
{
    private EtagMismatchException(string message)
        : base("ETAG_MISMATCH", StatusCodes.Status412PreconditionFailed, message)
    {
    }

    public static EtagMismatchException ForDocument() =>
        new("The document has been modified since it was last read.");

    public static EtagMismatchException ForNote() =>
        new("The note has been modified since it was last read.");

    public static EtagMismatchException ForExternalLink() =>
        new("The external link has been modified since it was last read.");

    public static EtagMismatchException ForBooking() =>
        new("The booking has been modified since it was last read.");

    // The entities tracked by #1083. Each names its own resource, because "it changed" is only actionable if
    // the reader knows WHAT changed — a settings page and a permission dialog need different next steps.
    public static EtagMismatchException ForCertificatePolicy() =>
        new("The certificate policy has been changed since it was last read.");

    public static EtagMismatchException ForTenant() =>
        new("The tenant's settings have been modified since they were last read.");

    public static EtagMismatchException ForUser() =>
        new("The user has been modified since they were last read.");

    public static EtagMismatchException ForServiceAccount() =>
        new("The service account has been modified since it was last read.");

    public static EtagMismatchException ForAclEntry() =>
        new("The permissions have been modified since they were last read.");

    public static EtagMismatchException ForWorkflow() =>
        new("The workflow has been modified since it was last read.");

    // #1220: entities that were EDITED from an admin form while carrying no token at all, so two people saving
    // the same row reverted each other in silence.
    public static EtagMismatchException ForGroup() =>
        new("The group has been modified since it was last read.");

    public static EtagMismatchException ForTag() =>
        new("The tag has been modified since it was last read.");

    public static EtagMismatchException ForPushRegistration() =>
        new("The phone's push registration has been modified since it was last read.");

    // ADR 0963: a trusted attestation root, renamed or switched by a second platform administrator meanwhile.
    public static EtagMismatchException ForAttestationRoot() =>
        new("The attestation root has been modified since it was last read.");

    // ADR 1015: a mail domain's roles, changed by a second administrator meanwhile.
    public static EtagMismatchException ForMailDomain() =>
        new("The mail domain has been modified since it was last read.");

    // ADR 1015: an identity alias, removed or replaced by a second administrator meanwhile.
    public static EtagMismatchException ForIdentityAlias() =>
        new("The identity alias has been modified since it was last read.");

    // #1970: a protocol device, renamed, switched or revoked in another session meanwhile.
    public static EtagMismatchException ForProtocolDevice() =>
        new("The device has been modified since it was last read.");
}
