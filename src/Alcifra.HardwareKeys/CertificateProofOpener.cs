using System.Security.Cryptography.X509Certificates;
using Alcifra.ApiClient;

namespace Alcifra.HardwareKeys;

/// <summary>
/// Opens a possession challenge with the card (ADR 1061): only the certificate being enrolled may answer. The ordinary
/// openers admit only keys already enrolled for the user (ADR 0969), which by definition this one is not.
/// </summary>
public sealed class CertificateProofOpener(X509Certificate2 certificate) : IEnvelopeOpener
{
    public async Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType)
    {
        var enveloped = SmimeEnvelope.Unwrap(envelope);
        return await CardEnvelopeOpener.TryOpen(enveloped, EnvelopeRecipients.Of(enveloped), c => c.Thumbprint == certificate.Thumbprint)
            ?? throw new EnvelopeNotOpenedException(CardEnvelopeOpener.WhatIsMissing());
    }
}
