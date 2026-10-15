using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Certificates;

/// <summary>No active module licenses certificate enrolment for this tenant (ADR 0936), so the surface does not exist.</summary>
public sealed class CertificateEnrolmentNotOfferedException : CertificateException
{
    public CertificateEnrolmentNotOfferedException()
        : base("CERTIFICATE_ENROLMENT_NOT_OFFERED", StatusCodes.Status404NotFound, "Certificate enrolment is not available for this tenant: no active module licenses it.")
    {
    }
}
