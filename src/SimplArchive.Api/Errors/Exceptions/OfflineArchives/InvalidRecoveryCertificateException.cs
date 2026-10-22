namespace SimplArchive.Api.Errors.Exceptions.OfflineArchives;

/// <summary>
/// The certificate offered as the tenant's recovery certificate cannot be one (ADR 0960): not a single readable
/// certificate, a key no envelope can be addressed to, or a PRIVATE KEY in the upload, which never belongs here.
/// </summary>
public sealed class InvalidRecoveryCertificateException : OfflineArchiveException
{
    public InvalidRecoveryCertificateException(string reason)
        : base("INVALID_RECOVERY_CERTIFICATE", StatusCodes.Status400BadRequest,
            $"The recovery certificate could not be used: {reason}. Upload the PUBLIC certificate alone (PEM), whose RSA or "
            + "elliptic-curve key can receive an envelope; its private key stays offline.")
    {
    }
}
