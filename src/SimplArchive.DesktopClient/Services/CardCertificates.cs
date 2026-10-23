using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using CAManagement.Pkcs11;
using CAManagement.Pkcs11.Configuration;
using CAManagement.Pkcs11.DataStructures;
using CAManagement.Pkcs11.Extensions;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Reads the certificates a PKCS#11 device carries, so one can be registered as the caller's own (#1398).
/// </summary>
/// <remarks>
/// <para>
/// The third way to register a certificate, beside uploading a <c>.pem</c> and generating an identity: point at
/// the token and register what is already on it. Only the PUBLIC half travels — the private key never leaves the
/// device, which is the entire reason to want this.
/// </para>
/// <para>
/// <b>The unit to walk is a SLOT, not "the card".</b> A PIV token has four key slots and may carry a certificate
/// in each; a non-PIV token may carry any number under any labels; and a user may have two readers plugged in.
/// So this enumerates every slot with a token present and every certificate on each, and lets the caller choose.
/// Searching by a known label — which is what the hardware proof did — would hard-code one vendor's slot naming
/// and show NOTHING on a token that names things differently.
/// </para>
/// <para>
/// <b>No PIN is collected, because none is needed.</b> Certificates are public objects: measured on a YubiKey,
/// enumerating them succeeds with no login. The same measurement is why this cannot report whether a matching
/// private key is present — <c>CKO_PRIVATE_KEY</c> objects stay invisible until <c>C_Login</c>, so asking would
/// mean a PIN prompt to answer a question the user did not ask. Registering a card certificate therefore carries
/// the same trust as uploading a <c>.pem</c>: the holder asserts it is theirs.
/// </para>
/// </remarks>
public static class CardCertificates
{
    /// <summary>A certificate found on a token, with what the user needs to recognise it.</summary>
    public sealed record Found(
        string TokenLabel,
        string TokenSerial,
        string ObjectLabel,
        X509Certificate2 Certificate,
        byte[]? KeyId = null)
    {
        /// <summary>The certificate in the PEM form the server registers.</summary>
        public string Pem => new(System.Security.Cryptography.PemEncoding.Write("CERTIFICATE", Certificate.RawData));
    }

    /// <summary>Set by the Browse fallback when the module is not where it is usually installed.</summary>
    public static string? ModulePathOverride { get; set; }

    /// <summary>Replaced in tests; the real one walks the token.</summary>
    public static Func<string, IReadOnlyList<Found>> Reader { get; set; } = ReadFromToken;

    /// <summary>
    /// Where a PKCS#11 module lives on each platform, in probe order — Yubico's own first, then OpenSC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probing rather than asking, because a file picker as the FIRST step means a user has to know what
    /// <c>opensc-pkcs11</c> is and where their platform put it — which for a showcase feature is most of the
    /// reason nobody would try it. The Browse fallback keeps the case where probing fails answerable.
    /// </para>
    /// <para>
    /// <b><c>libykcs11</c> IS PROBED FIRST, and it is not a preference — it is the only one that can do
    /// X25519.</b> Measured on a YubiKey 5.7.4 with an X25519 key in slot 9D: <c>libykcs11</c> derives the
    /// shared secret correctly, and <c>opensc-pkcs11</c> refuses the identical call with
    /// <c>CKR_KEY_TYPE_INCONSISTENT</c> — while enumerating the key perfectly happily as
    /// <c>EC_MONTGOMERY</c> with <c>Usage: derive</c>. So an OpenSC-first order means a modern card's
    /// key-agreement certificate is visible, registrable, and unusable.
    /// </para>
    /// <para>
    /// Ordering it first is safe for everyone else because it only EXISTS where Yubico's tooling was
    /// installed, and a module that finds no token it understands yields no certificates — which is the same
    /// answer as not being there. It is NOT a replacement: OpenSC serves every other card, so it stays, and
    /// stays for YubiKeys too as the fallback when Yubico's tooling is absent.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ModuleCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return
            [
                "/opt/homebrew/lib/libykcs11.dylib",    // yubico-piv-tool, Homebrew on Apple silicon
                "/usr/local/lib/libykcs11.dylib",       // yubico-piv-tool, Homebrew on Intel
                "/opt/homebrew/lib/opensc-pkcs11.so",   // Homebrew on Apple silicon
                "/usr/local/lib/opensc-pkcs11.so",      // Homebrew on Intel
                "/Library/OpenSC/lib/opensc-pkcs11.so", // the OpenSC .pkg installer
            ];
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return
            [
                @"C:\Program Files\Yubico\Yubico PIV Tool\bin\libykcs11.dll",
                @"C:\Program Files (x86)\Yubico\Yubico PIV Tool\bin\libykcs11.dll",
                @"C:\Program Files\OpenSC Project\OpenSC\pkcs11\opensc-pkcs11.dll",
                @"C:\Program Files (x86)\OpenSC Project\OpenSC\pkcs11\opensc-pkcs11.dll",
            ];
        }

        return
        [
            "/usr/lib/x86_64-linux-gnu/libykcs11.so",     // yubico-piv-tool, Debian/Ubuntu
            "/usr/lib64/libykcs11.so",                    // yubico-piv-tool, Fedora/RHEL
            "/usr/lib/x86_64-linux-gnu/opensc-pkcs11.so", // Debian/Ubuntu
            "/usr/lib64/opensc-pkcs11.so",                // Fedora/RHEL
            "/usr/lib/opensc-pkcs11.so",
            "/usr/lib/pkcs11/opensc-pkcs11.so",
        ];
    }

    /// <summary>The module to load, or null when none of the usual places has one.</summary>
    public static string? FindModule() =>
        ModulePathOverride is { Length: > 0 } chosen && File.Exists(chosen)
            ? chosen
            : ModuleCandidates().FirstOrDefault(File.Exists);

    /// <summary>Every certificate on every token currently present.</summary>
    public static IReadOnlyList<Found> Read(string modulePath) => Reader(modulePath);

    /// <summary>What is worth telling the user about a certificate before they register it.</summary>
    public enum Concerns
    {
        /// <summary>Nothing to say.</summary>
        None,

        /// <summary>Past its validity — the server will refuse it.</summary>
        Expired,

        /// <summary>Not yet valid.</summary>
        NotYetValid,

        /// <summary>Its stated key usage does not permit receiving an envelope.</summary>
        KeyUsage,
    }

    /// <summary>
    /// Why this certificate may not work as an envelope recipient — advisory, never a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An enum rather than a message, so the wording is translated with everything else rather than being
    /// English sentences compiled into a service.
    /// </para>
    /// <para>
    /// <b>A keyUsage mismatch is a WARNING on purpose.</b> The card this was built against carries a Key
    /// Management certificate stating <c>DigitalSignature</c> only, and it decrypts a real CMS envelope anyway:
    /// neither <c>EnvelopedCms</c> nor the token enforces keyUsage. A filter that refused on those grounds would
    /// refuse a certificate measured to work, so the user is told and left to decide. <c>Expired</c> is
    /// different in kind — the server itself refuses those — but it is still reported rather than hidden, so a
    /// user whose card has lapsed learns why instead of finding an empty list.
    /// </para>
    /// </remarks>
    public static Concerns Concern(X509Certificate2 certificate, DateTimeOffset now) =>
        certificate.NotAfter.ToUniversalTime() < now.UtcDateTime ? Concerns.Expired
        : certificate.NotBefore.ToUniversalTime() > now.UtcDateTime ? Concerns.NotYetValid
        : certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage
            // KeyAGREEMENT counts, and leaving it out was the same RSA-era assumption that nearly shipped in
            // the module's enrolment policy: an RSA recipient has the content key WRAPPED to it
            // (keyEncipherment), an EC recipient reaches it through ECDH AGREEMENT (keyAgreement). A card
            // issues the second, so warning on it told the holder of a perfectly good EC certificate that
            // their card was unsuitable.
            && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment)
            && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyAgreement)
            && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DataEncipherment)
                ? Concerns.KeyUsage
                : Concerns.None;

    /// <summary>
    /// The attestation a token offers for <paramref name="chosen"/>'s key (ADR 0963), from the certificates read with it, or
    /// null when it offers none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by the KEY, not by a label</b>, for the reason this class walks slots rather than names: a PKCS#11 module that
    /// exposes PIV attestation does so as further certificate objects, one per key slot carrying THAT slot's public key and
    /// signed by the token's own attestation certificate (PIV slot F9), which it exposes too. So the slot's attestation is
    /// the other certificate on the same token with the same public key, and the token's attestation certificate is the one
    /// whose subject is that certificate's issuer. Nothing here trusts what it finds; the server verifies the chain.
    /// </para>
    /// <para>
    /// <b>A key that was imported has no attestation</b>: the token attests only keys it generated, so a module finds no
    /// such certificate for it, and this answers null. The same holds for a module that exposes no attestation at all.
    /// </para>
    /// </remarks>
    public static SimplArchive.ApiClient.EnrolledCertificatesClient.KeyAttestation? AttestationFor(IReadOnlyList<Found> all, Found chosen)
    {
        var key = chosen.Certificate.PublicKey.ExportSubjectPublicKeyInfo();
        var sameToken = all.Where(f => f.TokenSerial == chosen.TokenSerial && f.TokenLabel == chosen.TokenLabel).ToList();
        var slot = sameToken.FirstOrDefault(f => !ReferenceEquals(f, chosen)
            && !f.Certificate.RawData.AsSpan().SequenceEqual(chosen.Certificate.RawData)
            && f.Certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key)
            && !f.Certificate.SubjectName.RawData.AsSpan().SequenceEqual(f.Certificate.IssuerName.RawData));
        var device = slot is null ? null : sameToken.FirstOrDefault(f => !ReferenceEquals(f, slot)
            && f.Certificate.SubjectName.RawData.AsSpan().SequenceEqual(slot.Certificate.IssuerName.RawData));
        return slot is null || device is null
            ? null
            : new(PivFormat, slot.Pem, device.Pem, SlotOf(slot.ObjectLabel));
    }

    /// <summary>
    /// The certificates a user may choose to register: everything read except the token's attestation EVIDENCE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A module that exposes PIV attestation lists, beside the slot's own certificate, the token's attestation
    /// certificate (F9) and one attestation per key slot — certificates nobody reads an envelope with. Offering them
    /// would let a user register evidence as their reader certificate, and counting them told the user a card held
    /// three certificates when it held one. They stay in <see cref="Read"/>'s result, because
    /// <see cref="AttestationFor"/> needs them.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Found> Choices(IReadOnlyList<Found> all) =>
        [.. all.Where(f => !IsEvidence(all, f))];

    /// <summary>The manufacturer's arc for PIV attestation extensions (firmware, serial, policies, form factor).</summary>
    private const string AttestationExtensionArc = "1.3.6.1.4.1.41482.3.";

    /// <summary>
    /// Whether <paramref name="candidate"/> is attestation evidence rather than a certificate to register.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The manufacturer's extensions first</b>: a slot attestation carries extensions under
    /// <c>1.3.6.1.4.1.41482.3</c>, which no reader certificate has a reason to carry.
    /// </para>
    /// <para>
    /// <b>Then the structure, never the label</b>, for a token that names its objects differently or a device
    /// certificate that carries none of those extensions. A SLOT attestation shares its public key with another
    /// certificate on the same token and was issued by a certificate on that token. The DEVICE attestation
    /// certificate issued such a slot attestation. Requiring the shared key is what keeps a user's own certificate
    /// visible when its issuing CA's certificate happens to sit on the token too: that CA issued a certificate, but
    /// not one that shares a key with another.
    /// </para>
    /// </remarks>
    private static bool IsEvidence(IReadOnlyList<Found> all, Found candidate)
    {
        if (CarriesAttestationExtension(candidate.Certificate))
        {
            return true;
        }

        var sameToken = all.Where(f => !ReferenceEquals(f, candidate)
            && f.TokenSerial == candidate.TokenSerial && f.TokenLabel == candidate.TokenLabel).ToList();
        return IsSlotAttestation(sameToken, candidate)
            || sameToken.Any(f => IssuedBy(f.Certificate, candidate.Certificate)
                && IsSlotAttestation([.. sameToken.Where(o => !ReferenceEquals(o, f)), candidate], f));
    }

    private static bool CarriesAttestationExtension(X509Certificate2 certificate) =>
        certificate.Extensions.Any(e => e.Oid?.Value?.StartsWith(AttestationExtensionArc, StringComparison.Ordinal) == true);

    // Shares its key with another certificate there, and was issued by one there.
    private static bool IsSlotAttestation(IReadOnlyList<Found> others, Found candidate)
    {
        var key = candidate.Certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return others.Any(o => !o.Certificate.RawData.AsSpan().SequenceEqual(candidate.Certificate.RawData)
                && o.Certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key))
            && others.Any(o => IssuedBy(candidate.Certificate, o.Certificate));
    }

    // Issued by ANOTHER certificate: a self-issued certificate names itself, which says nothing about the token.
    private static bool IssuedBy(X509Certificate2 subject, X509Certificate2 issuer) =>
        !subject.RawData.AsSpan().SequenceEqual(issuer.RawData)
        && subject.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData);

    /// <summary>The attestation format a PIV token's evidence is sent as.</summary>
    public const string PivFormat = "piv";

    // "X.509 Certificate for PIV Attestation 9d" names its slot last; anything else names none we can tell.
    private static string? SlotOf(string label) =>
        label.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() is { Length: 2 } last
            && last.All(Uri.IsHexDigit) ? last.ToLowerInvariant() : null;

    private static IReadOnlyList<Found> ReadFromToken(string modulePath) =>
        CardModule.Use(ReadFromLibrary, (IReadOnlyList<Found>)[]);

    /// <summary>
    /// The walk itself, over the module <see cref="CardModule"/> owns and inside its gate.
    /// </summary>
    /// <remarks>
    /// It used to load a library of its own, which was the second <c>C_Initialize</c> in the process and
    /// therefore the second half of <c>CKR_CRYPTOKI_ALREADY_INITIALIZED</c> — invisible until a real preview
    /// read the card from several tasks at once.
    /// </remarks>
    internal static IReadOnlyList<Found> ReadFromLibrary(Pkcs11Library library)
    {
        var found = new List<Found>();

        foreach (var slot in library.GetSlotList(tokenPresent: true))
        {
            // GUARDED PER SLOT, because a user may have two readers and only one of them a problem. Letting one
            // unreadable token abort the walk would report "the device could not be read" while a perfectly good
            // card sat in the other reader, with no way for the user to tell which was which.
            //
            // Written inline rather than as a helper taking the slot: a slot id is a CK_ULONG, whose CLR type is
            // `uint` on Windows and `ulong` everywhere else, so any signature naming it compiles for one target
            // and not the other. The compiler catches that — this project multi-targets for exactly this reason
            // (ADR 0831) — but the fix is to never name the type, not to spell it twice.
            try
            {
                var token = library.GetTokenInfo(slot);

                // Read-only, and no Login: certificates are public objects, so a PIN would be collected for
                // nothing — and asking for one would also be the only way to learn whether a matching private
                // key is present, which is a question the user did not ask.
                using var session = library.OpenSession(slot, readWrite: false);

                // SESSION OBJECTS TOO, or attestation never arrives (ADR 0963). The manufacturer's PIV module
                // exposes each key slot's attestation certificate ("X.509 Certificate for PIV Attestation 9d") as a
                // session object (CKA_TOKEN = false), created on demand; the token-only FindObjects returned the
                // slot's certificate and silently not its attestation, so AttestationFor answered null and the
                // enrolment went out unattested — measured with real hardware 2026-10-22. This walk only READS,
                // so widening it costs nothing a writer would care about.
                foreach (var handle in session.FindObjectsIncludingSessionObjects(CK_OBJECT_CLASS.CKO_CERTIFICATE))
                {
                    X509Certificate2 certificate;
                    string label;
                    try
                    {
                        certificate = X509CertificateLoader.LoadCertificate(
                            session.GetAttributeValue(handle, CK_ATTRIBUTE_TYPE.CKA_VALUE));
                        label = session.GetLabel(handle);
                    }
                    catch (Exception)
                    {
                        // An object that says it is a certificate and does not parse is the token's problem, not
                        // a reason to abandon the ones that do — a user with three certificates should not lose
                        // all three to one bad object.
                        continue;
                    }

                    // CKA_ID TIES THE CERTIFICATE TO ITS KEY, and it is the only thing that does: a PIV
                    // token has four key slots, so "the first private key on this token" is a guess that is
                    // right by luck on a card where only one slot is used (#1500). Optional because a token
                    // that does not set it still works — the opener falls back and says so.
                    byte[]? keyId = null;
                    try
                    {
                        keyId = session.GetAttributeValue(handle, CK_ATTRIBUTE_TYPE.CKA_ID);
                    }
                    catch (Exception)
                    {
                        // No CKA_ID on this object. Not a reason to drop a certificate that reads perfectly.
                    }

                    found.Add(new Found(
                        token.Label.AsPkcs11String().Trim(),
                        token.SerialNumber.AsPkcs11String().Trim(),
                        label,
                        certificate,
                        keyId));
                }
            }
            catch (Exception e)
            {
                DesktopLog.Warn(e, "Reading a slot of the PKCS#11 module failed");
            }
        }

        return found;
    }
}
