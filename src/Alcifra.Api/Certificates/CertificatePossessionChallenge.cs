using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Alcifra.Api.Errors.Exceptions.Certificates;
using Alcifra.Messenger.Client;

namespace Alcifra.Api.Certificates;

/// <summary>What a member sends back to prove they hold a key: the challenge's token, and the nonce they opened.</summary>
public sealed record PossessionProof(string Token, string Nonce);

/// <summary>
/// Self-enrolment under Possession (ADR 1061): a random nonce is sealed to the offered certificate, and whoever opens it
/// holds the key. It proves what a reader certificate is for, and needs no new key operation in any client.
/// </summary>
/// <remarks>Stateless: the token carries who, which certificate, the nonce's hash and the end, protected by Data
/// Protection, so no table is needed and every instance accepts it. A replay inside the window only proves the same
/// possession by the same user again.</remarks>
public sealed class CertificatePossessionChallenge(IDataProtectionProvider protection, TimeProvider clock)
{
    private const string Purpose = "Alcifra.Certificates.PossessionChallenge.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private sealed record Claims(Guid Tenant, Guid User, string Thumbprint, string NonceHash, DateTimeOffset Until);

    /// <summary>The envelope to open and the token to return with what it held.</summary>
    public (byte[] Envelope, string Token) Issue(Guid tenant, Guid user, X509Certificate2 certificate)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var claims = new Claims(tenant, user, certificate.Thumbprint, Hash(nonce), clock.GetUtcNow().Add(Lifetime));
        return (ConversationKeys.SealTo(AsMime(nonce), [certificate]), protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(claims)));
    }

    /// <summary>Refuses, each with its own exception, anything but this user's answer for this certificate in time.</summary>
    public void Verify(PossessionProof proof, Guid tenant, Guid user, X509Certificate2 certificate)
    {
        Claims claims;
        try
        {
            claims = JsonSerializer.Deserialize<Claims>(protection.CreateProtector(Purpose).Unprotect(proof.Token))
                ?? throw new CertificateChallengeForeignException();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            throw new CertificateChallengeForeignException();
        }

        if (claims.Tenant != tenant || claims.User != user)
        {
            throw new CertificateChallengeForeignException();
        }

        if (clock.GetUtcNow() > claims.Until)
        {
            throw new CertificateChallengeExpiredException();
        }

        if (!string.Equals(claims.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new CertificateChallengeOtherCertificateException();
        }

        byte[] nonce;
        try
        {
            nonce = Convert.FromBase64String(proof.Nonce);
        }
        catch (FormatException)
        {
            throw new CertificateChallengeWrongAnswerException();
        }

        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(nonce)), Convert.FromHexString(claims.NonceHash)))
        {
            throw new CertificateChallengeWrongAnswerException();
        }
    }

    // The shape every client's opener reads (SmimeEnvelope.Payload): one part, its body the nonce.
    private static byte[] AsMime(byte[] nonce) => System.Text.Encoding.ASCII.GetBytes(
        $"Content-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\n\r\n{Convert.ToBase64String(nonce)}\r\n");

    private static string Hash(byte[] nonce) => Convert.ToHexString(SHA256.HashData(nonce));
}
