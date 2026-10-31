using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>A certificate policy named a binding that does not exist (ADR 1017).</summary>
public sealed class CertificateBindingUnknownException(string binding)
    : CertificateException("CERTIFICATE_BINDING_UNKNOWN", StatusCodes.Status400BadRequest,
        $"'{binding}' is not a certificate binding. Choose strict (a certificate carries its holder's address) or possession (an administrator enrols the key for its holder).");
