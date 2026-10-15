using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SimplArchive.Infrastructure.Certificates.Policy;

/// <summary>A generic rule: a pattern one of a certificate's addressable values must match.</summary>
/// <param name="Element">An address <see cref="CertificateElement"/> can resolve.</param>
/// <param name="Pattern">A .NET regular expression. Anchoring is the author's business.</param>
public sealed record CertificateRule(string Element, string Pattern);

/// <summary>
/// What a tenant requires of a certificate before it may be enrolled (Encryption Module ADR 0006).
/// </summary>
/// <remarks>
/// <para>
/// <b>Typed constraints AND a generic list</b>, which is one decision rather than two mechanisms competing.
/// The typed ones exist for the REFUSAL MESSAGE: a named constraint can say which rule failed and what it
/// wanted, which is the difference between a user re-enrolling correctly and a user filing a ticket. A purely
/// generic mechanism can only say "rule 3 did not match". The generic list exists because <i>"any other
/// element"</i> was the requirement as stated, and waiting for a release to express one rule is worse than a
/// rule whose failure message is vaguer.
/// </para>
/// <para>
/// <b>An EMPTY policy admits everything</b>, and that is the shape an unconfigured tenant has. It must be,
/// because this type is read on a path that already worked before policies existed: a tenant that never
/// opens the policy surface keeps exactly Encryption Module ADR 0004's cryptographic checks and nothing more.
/// </para>
/// </remarks>
public sealed record CertificatePolicy
{
    // RECORD EQUALITY IS NOT MEANINGFUL ON THIS TYPE, the same way it is not on the ABI's TenantLicense and
    // for the same reason: two of these properties are lists, and a record compares a list by REFERENCE — so
    // a policy parsed back from its own stored form never equals the one it came from. Compare ToJson() when
    // identity matters, which is also what a change-detection check would want.

    /// <summary>The policy of a tenant that has not set one — Encryption Module ADR 0004's checks plus the product floor.</summary>
    /// <remarks>
    /// <b>"Unset" no longer means unconstrained</b>, and the distinction is the point: a tenant that has
    /// stated nothing still gets <see cref="DefaultMinimumRsaKeySizeBits"/> and
    /// <see cref="DefaultPermittedCurves"/>, because the alternative was an installation with no key floor
    /// whatsoever. What is unset is the TENANT's additions; the floor is the product's.
    /// </remarks>
    public static readonly CertificatePolicy Unset = new();

    /// <summary>A pattern the issuer's whole distinguished name must match.</summary>
    public string? IssuerDnPattern { get; init; }

    /// <summary>A pattern the subject's common name must match.</summary>
    public string? SubjectCnPattern { get; init; }

    /// <summary>Whether an e-mail on the certificate must match the enrolling account's.</summary>
    public bool SanMustMatchAccount { get; init; }

    /// <summary>
    /// The smallest acceptable RSA key, in bits — <b>RSA only</b>. Null means the product default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>RSA only, because one number cannot police both algorithms.</b> A P-256 key measures 256 bits and
    /// an RSA-2048 key measures 2048, and they are comparable in neither direction: 256 is a perfectly
    /// strong elliptic-curve key and a catastrophic RSA one. A single minimum set to 2048 refuses every EC
    /// certificate — including a PIV card's, which is the ordinary case this product serves — and set to 256
    /// permits RSA-256. So the RSA floor is a number and the EC floor is a list of curves
    /// (<see cref="PermittedCurves"/>).
    /// </para>
    /// <para>
    /// <b>Null means <see cref="DefaultMinimumRsaKeySizeBits"/>, not "no floor"</b> (owner-decided
    /// 2026-09-30). The field used to be applied only when a tenant set it, so an installation that never
    /// wrote a policy had no minimum at all — and each recipient of an envelope is an independent door, so
    /// one weak key is a weak door on every document that reader can open, while the AES-256 content
    /// encryption and every other recipient look perfectly healthy. A tenant that genuinely wants a lower
    /// floor states a lower number; absence can no longer mean absence.
    /// </para>
    /// </remarks>
    public int? MinimumKeySizeBits { get; init; }

    /// <summary>
    /// The elliptic curves this tenant accepts, by name or OID — empty means
    /// <see cref="DefaultPermittedCurves"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The EC half of the floor above. Spellings are <b>BouncyCastle's registry names</b> (owner-decided) —
    /// <c>secp256r1</c>, <c>secp384r1</c>, <c>secp521r1</c> — so a tenant naming a curve writes a name that
    /// exists rather than one somebody invented; <c>prime256v1</c> (BouncyCastle's documented alias for
    /// secp256r1) and the NIST <c>P-256</c> form are accepted too, as are raw OIDs, because a certificate
    /// tool may print any of them and refusing a correct curve over its spelling is a refusal nobody can act
    /// on.
    /// </para>
    /// <para>
    /// A LIST rather than a minimum size: curves are not ordered by strength in a way a number captures, and
    /// the ones worth refusing are refused for their provenance rather than their size.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> PermittedCurves { get; init; } = [];

    /// <summary>The RSA floor an installation has when its tenant has stated none.</summary>
    /// <remarks>
    /// 2048 rather than 3072: it is the smallest size still generally accepted, and every PIV card and
    /// software keystore in use issues at least it — a floor that refuses hardware people already hold is a
    /// floor that gets lowered rather than met.
    /// </remarks>
    public const int DefaultMinimumRsaKeySizeBits = 2048;

    /// <summary>The curves an installation accepts when its tenant has named none.</summary>
    /// <remarks>
    /// The three NIST prime curves, which is what PIV issues and what .NET and every card can agree on.
    /// Curve25519 is deliberately absent: the service's ADR 0016 prefers it for identity keys and defers it
    /// on the card, and admitting it here would advertise a path the delivery leg cannot yet complete.
    /// </remarks>
    public static IReadOnlyList<string> DefaultPermittedCurves { get; } = ["secp256r1", "secp384r1", "secp521r1"];

    /// <summary>Extended key usages the certificate must carry, by OID.</summary>
    public IReadOnlyList<string> RequiredExtendedKeyUsages { get; init; } = [];

    /// <summary>A trust anchor the certificate's chain must reach — the "only our own CA" rule.</summary>
    public string? TrustAnchorPem { get; init; }

    /// <summary>Everything the typed constraints do not cover.</summary>
    public IReadOnlyList<CertificateRule> Rules { get; init; } = [];

    /// <summary>True when this policy asks for nothing beyond Encryption Module ADR 0004's own checks.</summary>
    [JsonIgnore]
    public bool IsUnset =>
        IssuerDnPattern is null && SubjectCnPattern is null && !SanMustMatchAccount
        && MinimumKeySizeBits is null && PermittedCurves.Count == 0 && RequiredExtendedKeyUsages.Count == 0
        && TrustAnchorPem is null && Rules.Count == 0;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored form — a policy is a DOCUMENT (core ADR 0738: a read model holds derived data only).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads a stored policy, treating an unreadable one as UNSET rather than throwing.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the direction matters: this is read on the enrolment path, and a policy document
    /// somebody edited by hand into invalid JSON must not make enrolment impossible — it must make it
    /// unconstrained, which is the behaviour of a tenant that never set one. The surface is what refuses bad
    /// input; this is the last line, and the safe failure here is "no extra requirements" rather than "no
    /// enrolment at all".
    /// <para>
    /// The opposite choice was considered: refusing every enrolment on a corrupt policy is arguably safer for
    /// a tenant that MEANT to constrain. It was rejected because the failure is indistinguishable at the door
    /// from the policy being strict, and an administrator would be debugging a rule they cannot see.
    /// </para>
    /// </remarks>
    public static CertificatePolicy FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Unset
            : TryParse(json) ?? Unset;

    private static CertificatePolicy? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CertificatePolicy>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What is wrong with this policy, for the surface to refuse before storing it — empty when it is usable.
    /// </summary>
    /// <remarks>
    /// <b>This is why the policy has a surface of its own rather than a settings text box</b> (Encryption Module ADR 0006): a
    /// rule whose regex does not compile, or whose element nothing resolves, matches NOTHING — and on a
    /// sealed-delivery tenant a policy matching nothing locks every reader out of their own documents, with
    /// saving an admin form as the act that did it. Refused where it is typed, or not at all.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        foreach (var (pattern, name) in new[]
        {
            (IssuerDnPattern, nameof(IssuerDnPattern)),
            (SubjectCnPattern, nameof(SubjectCnPattern)),
        })
        {
            if (pattern is { Length: > 0 } && !Compiles(pattern))
            {
                problems.Add($"{name} is not a valid regular expression.");
            }
        }

        if (MinimumKeySizeBits is { } bits and (< 1024 or > 16384))
        {
            // Bounded on BOTH sides on purpose: a floor below 1024 is not a policy, and a ceiling nothing can
            // satisfy is the lock-out this method exists to prevent — a typo of 40960 would refuse every
            // certificate in existence.
            problems.Add($"MinimumKeySizeBits must be between 1024 and 16384; got {bits}.");
        }

        foreach (var oid in RequiredExtendedKeyUsages.Where(o => !LooksLikeOid(o)))
        {
            problems.Add($"RequiredExtendedKeyUsages contains '{oid}', which is not an OID.");
        }

        if (TrustAnchorPem is { Length: > 0 } anchor && !LoadsAsCertificate(anchor))
        {
            problems.Add("TrustAnchorPem is not a readable certificate.");
        }

        for (var i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i];
            if (!CertificateElement.IsKnown(rule.Element))
            {
                problems.Add(
                    $"Rule {i + 1} addresses '{rule.Element}', which this module cannot resolve. "
                    + $"Use one of: {CertificateElement.Describe()}.");
            }

            if (!Compiles(rule.Pattern))
            {
                problems.Add($"Rule {i + 1}'s pattern is not a valid regular expression.");
            }
        }

        return problems;
    }

    private static bool Compiles(string pattern)
    {
        try
        {
            // Constructed rather than merely parsed, and with a timeout: a stored pattern is evaluated per
            // certificate per listing, so a catastrophically backtracking one would be a denial of service an
            // administrator could save by accident.
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool LoadsAsCertificate(string pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool LooksLikeOid(string value) =>
        value.Split('.') is { Length: >= 2 } parts
        && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
}
