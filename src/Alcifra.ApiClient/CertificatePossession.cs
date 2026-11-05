using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.ApiClient;

/// <summary>
/// The client's half of self-enrolment under Possession (ADR 1061): ask for a challenge, open it with the key of the
/// certificate being enrolled, and hand back what the enrolment carries.
/// </summary>
public static class CertificatePossession
{
    /// <summary>The refusal that means a proof is wanted. An older server says the same and offers no challenge.</summary>
    public const string AskedFor = "CERTIFICATE_POSSESSION_NEEDS_ADMINISTRATOR";

    private const string EnvelopeType = "application/pkcs7-mime";

    /// <summary>What an enrolment sends as <c>possession</c>.</summary>
    public sealed record Proof(string Token, string Nonce);

    /// <summary>Runs the challenge. The opener's refusal, a cancelled PIN or a missing key, reaches the caller as it is.</summary>
    public static async Task<Proof> ProveAsync(ApiCore core, string certificatePem, IEnvelopeOpener opener, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(
            await core.RootHrefAsync("certificateChallenge", cancellationToken), new { certificatePem }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var challenge = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var envelope = Convert.FromBase64String(challenge.GetProperty("envelope").GetString()!);
        var (nonce, _) = await opener.OpenAsync(PasswordStoreClient.AsSmime(envelope), EnvelopeType);
        return new Proof(challenge.GetProperty("token").GetString()!, Convert.ToBase64String(nonce));
    }
}
