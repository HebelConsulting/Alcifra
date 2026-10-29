namespace Alcifra.Api.Errors.Exceptions.Certificates;

// Base class for certificate enrolment errors (ADR 0936): the codes the Encryption Module raised as ENCRYPTION_*
// before the core owned enrolment. Concrete errors inherit from this so a caller can catch the whole area.
public abstract class CertificateException : ApiException
{
    protected CertificateException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
