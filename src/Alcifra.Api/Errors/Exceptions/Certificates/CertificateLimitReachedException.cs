using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>
/// The holder already has as many unrevoked certificates as this tenant allows one user without a licence for more
/// (#1970 slice 4): revoking one makes room.
/// </summary>
public sealed class CertificateLimitReachedException : CertificateException
{
    public CertificateLimitReachedException(int limit)
        : base("CERTIFICATE_LIMIT_REACHED", StatusCodes.Status409Conflict,
            $"This person already holds {limit} enrolled certificates, the most allowed here; revoke one before enrolling another.")
    {
    }
}
