namespace Alcifra.Presentation;

/// <summary>
/// Which sentence describes an enrolled certificate's attestation (ADR 0963), so the web, the desktop and the phone say the
/// same thing about the same certificate. The clients own the words (the keys) and the formatting.
/// </summary>
public static class CertificateAttestationText
{
    /// <summary>The localization key for a certificate's attestation state, and whether the root's name fills its <c>{0}</c>.</summary>
    /// <param name="attestation">The listing's <c>attestation</c>: "attested", "rootNotTrusted" or "notAttested".</param>
    /// <param name="usable">The listing's <c>usable</c>: whether it addresses its holder now.</param>
    public static (string Key, bool NamesRoot) For(string attestation, bool usable) => attestation switch
    {
        "attested" => ("EnrolAttestedBy", true),
        // Not trusted here, but the tenant admits hardware without attestation: it still addresses its holder.
        "rootNotTrusted" when usable => ("EnrolNotAttestedAllowed", false),
        "rootNotTrusted" => ("EnrolRootNotTrusted", true),
        _ when usable => ("EnrolNotAttestedAllowed", false),
        _ => ("EnrolNotAttested", false),
    };
}
