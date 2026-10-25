namespace SimplArchive.UnitTests;

// The desktop's card walk must find certificates that are SESSION objects, or attestation never arrives (ADR 0963).
//
// Measured with real hardware 2026-10-22: the manufacturer's PIV module exposes each key slot's attestation certificate
// ("X.509 Certificate for PIV Attestation 9d") as a session object (CKA_TOKEN = false), created on demand. The token-only
// `FindObjects(CKO_CERTIFICATE)` returned the slot's certificate and silently not its attestation, so AttestationFor
// answered null and the desktop enrolled without attestation — which ADR 0963 refuses.
//
// WHY THIS IS A SOURCE TEST. The desktop's seam (`CardCertificates.Reader`) replaces the whole walk, so a fake reader
// hands over certificates with no notion of token versus session objects; only a PKCS#11 module has that, and SoftHSM in
// this suite would prove things about SoftHSM. The behaviour itself is covered in the package's own SoftHSM test
// (CAManagement 0.9.0, `Session_objects_are_found_only_by_the_call_that_includes_them`); what can regress HERE is a
// person switching the call back, so that is what this asserts.
public class CardCertificatesSeeSessionObjectsTests
{
    private static string Source() => File.ReadAllText(Path.Combine(
        RepoPaths.Root(), "src", "SimplArchive.HardwareKeys", "CardCertificates.cs"));

    private static IEnumerable<string> Code(string source) => source
        .Split('\n')
        .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

    [Fact]
    public void The_card_walk_enumerates_certificates_including_session_objects()
    {
        var code = Code(Source()).ToList();

        Assert.Contains(code, line => line.Contains(
            "FindObjectsIncludingSessionObjects(CK_OBJECT_CLASS.CKO_CERTIFICATE)", StringComparison.Ordinal));
        Assert.DoesNotContain(code, line => line.Contains(
            ".FindObjects(CK_OBJECT_CLASS.CKO_CERTIFICATE", StringComparison.Ordinal));
    }
}
