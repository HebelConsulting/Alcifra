namespace SimplArchive.Api.Errors.Exceptions.AttestationRoots;

// Base class for the installation's trusted attestation roots (ADR 0963), a platform administrator's surface.
public abstract class AttestationRootException : ApiException
{
    protected AttestationRootException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
