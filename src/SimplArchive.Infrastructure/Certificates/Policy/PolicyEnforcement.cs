using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace SimplArchive.Infrastructure.Certificates.Policy;

/// <summary>
/// Applies a tenant's policy to a certificate, naming what failed (Encryption Module ADR 0006).
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <c>EnrolmentPolicy</c> on purpose.</b> That one applies the rules of the PRODUCT — no
/// private key, a readable chain, a key usage that can receive an envelope (Encryption Module ADR 0004) — which every tenant
/// gets and none can change. This applies the rules of a TENANT. Keeping them apart is what lets the
/// refusals say whose rule was broken, and keeps a tenant from being able to switch off a check the product
/// requires.
/// </para>
/// <para>
/// <b>Each typed constraint has its own message, and that is the reason they are typed at all.</b> A generic
/// mechanism can only say "rule 3 did not match", which is the difference between a user re-enrolling
/// correctly and a user filing a ticket.
/// </para>
/// </remarks>
public static class PolicyEnforcement
{
    /// <summary>A pattern is evaluated with a timeout, so a stored one cannot hang a read.</summary>
    private static readonly TimeSpan PatternBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Why this certificate does not satisfy the policy, or null when it does.
    /// </summary>
    /// <param name="policy">The tenant's policy.</param>
    /// <param name="certificate">The leaf being enrolled.</param>
    /// <param name="accountEmail">
    /// The enrolling account's address, for <see cref="CertificatePolicy.SanMustMatchAccount"/>.
    /// </param>
    /// <param name="chainPem">
    /// The whole filed bundle, so a trust-anchor rule can build the chain the reader will actually present.
    /// </param>
    public static string? Refusal(
        CertificatePolicy policy, X509Certificate2 certificate, string accountEmail, string chainPem)
    {
        // THE KEY-STRENGTH FLOOR IS THE PRODUCT'S AND RUNS FIRST, before the short-circuit below — a tenant
        // that has stated nothing still gets it (owner-decided 2026-09-30). Putting it after `IsUnset` is
        // what made it dead for exactly the installations with no policy, which are the ones that need a
        // floor rather than an override.
        if (KeyStrengthRefusal(policy, certificate) is { } weak)
        {
            return weak;
        }

        if (policy.IsUnset)
        {
            // The rest is the TENANT's additions, and a tenant that never opened the policy surface pays
            // nothing for the feature existing.
            return null;
        }

        if (policy.IssuerDnPattern is { Length: > 0 } issuer && !Matches(issuer, certificate.Issuer))
        {
            return $"This tenant only accepts certificates whose issuer matches '{issuer}'. "
                + $"This one was issued by '{certificate.Issuer}'.";
        }

        if (policy.SubjectCnPattern is { Length: > 0 } cn)
        {
            var subjectCn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? string.Empty;
            if (!Matches(cn, subjectCn))
            {
                return $"This tenant requires the certificate's common name to match '{cn}'. "
                    + $"This one is '{subjectCn}'.";
            }
        }

        if (policy.SanMustMatchAccount)
        {
            // The certificate's OWN idea of its address, read the way the enveloper reads it — so a
            // certificate that passes here is one the enveloper will address to the same person.
            var onCertificate = certificate.GetNameInfo(X509NameType.EmailName, forIssuer: false);
            if (!string.Equals(onCertificate, accountEmail, StringComparison.OrdinalIgnoreCase))
            {
                return "This tenant requires a certificate to carry the e-mail address of the account it is "
                    + $"enrolled for. This one says '{onCertificate ?? "(no address)"}' and the account is "
                    + $"'{accountEmail}'.";
            }
        }

        if (policy.RequiredExtendedKeyUsages.Count > 0 && MissingEku(certificate, policy) is { } missing)
        {
            return $"This tenant requires the extended key usage {missing}, which this certificate does not "
                + "declare.";
        }

        if (policy.TrustAnchorPem is { Length: > 0 } anchor && !ChainsTo(certificate, anchor, chainPem))
        {
            return "This tenant only accepts certificates issued by its own certification authority, and "
                + "this one does not chain to it. Enrol a certificate your administrator issued.";
        }

        for (var i = 0; i < policy.Rules.Count; i++)
        {
            var rule = policy.Rules[i];
            var values = CertificateElement.ValuesOf(certificate, rule.Element);

            // ANY value matching passes: a certificate carrying two DNS names is not less the tenant's for
            // having two, and requiring all of them would refuse certificates the policy was written to admit.
            if (!values.Any(value => Matches(rule.Pattern, value)))
            {
                return $"This tenant requires {rule.Element} to match '{rule.Pattern}'. This certificate's "
                    + (values.Count == 0
                        ? $"{rule.Element} is absent."
                        : $"is '{string.Join("', '", values)}'.");
            }
        }

        return null;
    }

    /// <summary>
    /// Why an ALREADY-ENROLLED certificate would no longer be accepted under this policy, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>FLAG, DON'T BREAK</b> (Encryption Module ADR 0006). Tightening a rule must not stop anything working: an enrolled
    /// certificate keeps being used, and this is how an administrator learns which ones would no longer be
    /// accepted. The severity is the reason — on a sealed-delivery tenant a reader whose certificate is
    /// rejected cannot read ANYTHING, so enforcing immediately would make saving an admin form a way to lock
    /// colleagues out of their own documents, with nothing about saving to suggest it.
    /// </para>
    /// <para>
    /// <b>Evaluated per LISTING, never stored.</b> A flag projected onto the read-model row would go stale
    /// the moment the policy document changed, and correcting that means re-running the certificate
    /// projection on every policy edit — the hook is per-changed-document (core ADR 0848), so it would need a
    /// rebuild trigger nothing else wants. A few regexes over a few rows cannot be stale.
    /// </para>
    /// <para>
    /// <b><see cref="CertificatePolicy.SanMustMatchAccount"/> is deliberately NOT applied here</b>, and it is
    /// switched off explicitly rather than left to a blank address — passing an empty account would make that
    /// constraint fail for every row, lighting the whole column up on exactly the tenants that set it. It
    /// asks whether a certificate carries the address of the account it was enrolled FOR, and the read model
    /// keys on the user id rather than carrying that address; re-deriving it would be guessing. So it is
    /// enforced at enrolment only, which is stated here because a reader would otherwise wonder why a
    /// mismatched address does not show up.
    /// </para>
    /// <para>
    /// A certificate whose stored bundle no longer parses is reported as failing rather than throwing: this
    /// is a listing, and one unreadable row must not take the pane down.
    /// </para>
    /// </remarks>
    public static string? RefusalForEnrolled(CertificatePolicy policy, string certificatePem)
    {
        // NO IsUnset SHORT-CIRCUIT HERE ANY MORE. The product's key-strength floor applies to a tenant that
        // has set nothing, and this is the listing's flag — so a certificate enrolled before the floor
        // existed, or under a policy that was later tightened, has to be shown as failing it. Short-circuiting
        // would leave the strongest reason a certificate is unusable as the one reason never displayed.
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            return Refusal(
                policy with { SanMustMatchAccount = false },
                certificate,
                accountEmail: string.Empty,
                chainPem: certificatePem);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or ArgumentException or FormatException)
        {
            return "This certificate can no longer be read.";
        }
    }

    /// <summary>
    /// Whether a pattern matches, treating a pattern that misbehaves as NOT matching.
    /// </summary>
    /// <remarks>
    /// A timeout means a stored pattern cannot hang the enrolment path or a listing. Failing closed on
    /// timeout is the safe direction here — the alternative admits a certificate because the rule meant to
    /// exclude it took too long, which is a rule that stops working under exactly the input it was aimed at.
    /// </remarks>
    private static bool Matches(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, PatternBudget);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // An invalid pattern cannot be stored through the surface, so this is a hand-edited document.
            // Not matching keeps the tenant's intent (a rule exists, so be strict) without throwing inside
            // a read.
            return false;
        }
    }

    /// <summary>
    /// Whether this key is strong enough, asked PER ALGORITHM — or null when it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One number cannot police both algorithms</b>, which is why this replaced a single minimum applied
    /// to whatever <c>KeySize</c> returned. A P-256 key measures 256 bits and an RSA-2048 key measures 2048:
    /// a floor of 2048 refuses every EC certificate — a PIV card's included, which is the ordinary case —
    /// and a floor of 256 permits RSA-256. So RSA gets a number and EC gets a curve list.
    /// </para>
    /// <para>
    /// <b>Absence means the product default, never "no check".</b> The old shape applied the minimum only
    /// when a tenant had set one, so an installation that never wrote a policy had no floor at all. Each
    /// recipient of an envelope is an independent door (ADR 0842 addresses the whole set), so one weak key is
    /// a weak door on every document that reader can open — while the content encryption and every other
    /// recipient look perfectly healthy, and nothing reports it.
    /// </para>
    /// <para>
    /// A key this cannot measure at all is PERMITTED rather than refused: the algorithm is neither RSA nor
    /// EC, Encryption Module ADR 0004's own checks have already passed it, and refusing it here would be refusing something
    /// this rule has no opinion about.
    /// </para>
    /// </remarks>
    private static string? KeyStrengthRefusal(CertificatePolicy policy, X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            var minimum = policy.MinimumKeySizeBits ?? CertificatePolicy.DefaultMinimumRsaKeySizeBits;
            return rsa.KeySize >= minimum
                ? null
                : $"An RSA key of at least {minimum} bits is required here. This certificate's is "
                    + $"{rsa.KeySize}, which is too weak to address a sealed document to — every recipient of "
                    + "an envelope is an independent way in, so this key would be the weakest.";
        }

        if (CurveOf(certificate) is not { } curve)
        {
            return null;   // not RSA, not EC, and not this rule's business
        }

        var permitted = policy.PermittedCurves.Count > 0
            ? policy.PermittedCurves
            : CertificatePolicy.DefaultPermittedCurves;

        // Matched on the OID *or* any accepted spelling of the name, in both directions: a certificate may
        // report a curve as an OID on one platform and a friendly name on another, and a tenant may have
        // written either. Refusing a correct curve over its spelling is a refusal nobody can act on.
        return permitted.Any(p => Names(p).Any(n => curve.Contains(n, StringComparer.OrdinalIgnoreCase)))
            ? null
            : $"This certificate's elliptic curve ({curve.First()}) is not one this installation accepts. "
                + $"Permitted: {string.Join(", ", permitted)}.";
    }

    /// <summary>Every spelling of the certificate's curve — its OID and its friendly name, where known.</summary>
    /// <remarks>
    /// Read through whichever key getter answers, and <b>ECDsa answers for a <c>keyAgreement</c>-only
    /// certificate too</b>: .NET hands back the key for any <c>id-ecPublicKey</c> certificate regardless of
    /// the usage it declares, which is what makes asking in this order safe. An explicit (non-named) curve
    /// reports no OID and no name, and is treated as unknown rather than guessed at.
    /// </remarks>
    private static IReadOnlyList<string>? CurveOf(X509Certificate2 certificate)
    {
        ECParameters parameters;
        using (var ecdsa = certificate.GetECDsaPublicKey())
        using (var ecdh = certificate.GetECDiffieHellmanPublicKey())
        {
            if (ecdsa is not null)
            {
                parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            }
            else if (ecdh is not null)
            {
                parameters = ecdh.ExportParameters(includePrivateParameters: false);
            }
            else
            {
                return null;
            }
        }

        var spellings = new List<string>();
        if (parameters.Curve.Oid.Value is { Length: > 0 } oid)
        {
            spellings.Add(oid);
            spellings.AddRange(Names(oid));
        }

        if (parameters.Curve.Oid.FriendlyName is { Length: > 0 } friendly)
        {
            spellings.Add(friendly);
            spellings.AddRange(Names(friendly));
        }

        return spellings.Count > 0 ? spellings : null;
    }

    /// <summary>
    /// Every accepted spelling of one curve — BouncyCastle's registry name, its aliases, and the OID.
    /// </summary>
    /// <remarks>
    /// The canonical spellings are BouncyCastle's (<c>secpNNNr1</c>), which is what a tenant is asked to
    /// write; <c>prime256v1</c> is its documented alias for secp256r1 and the NIST <c>P-NNN</c> forms are
    /// what .NET and most tools print. Pinned here rather than read from BouncyCastle at runtime because a
    /// module ships as its OWN assembly alone (core ADR 0741) — a dependency it cannot deploy would be a
    /// TypeLoadException at load, and these three curves' identifiers are not going to change.
    /// </remarks>
    private static IReadOnlyList<string> Names(string spelling) => spelling.ToLowerInvariant() switch
    {
        "1.2.840.10045.3.1.7" or "secp256r1" or "prime256v1" or "p-256" or "nistp256" or "ecdsa_p256"
            => ["1.2.840.10045.3.1.7", "secp256r1", "prime256v1", "p-256", "nistp256", "ecdsa_p256"],
        "1.3.132.0.34" or "secp384r1" or "p-384" or "nistp384" or "ecdsa_p384"
            => ["1.3.132.0.34", "secp384r1", "p-384", "nistp384", "ecdsa_p384"],
        "1.3.132.0.35" or "secp521r1" or "p-521" or "nistp521" or "ecdsa_p521"
            => ["1.3.132.0.35", "secp521r1", "p-521", "nistp521", "ecdsa_p521"],
        _ => [spelling],
    };

    private static string? MissingEku(X509Certificate2 certificate, CertificatePolicy policy)
    {
        var declared = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>())
            .Select(o => o.Value)
            .Where(v => v is not null)
            .ToHashSet(StringComparer.Ordinal);

        return policy.RequiredExtendedKeyUsages.FirstOrDefault(required => !declared.Contains(required));
    }

    private static bool ChainsTo(X509Certificate2 leaf, string anchorPem, string chainPem)
    {
        try
        {
            using var anchor = X509Certificate2.CreateFromPem(anchorPem);
            using var chain = new X509Chain();

            // CUSTOM trust, and only the tenant's anchor: the machine's own root store is irrelevant to
            // "issued by OUR CA", and including it would admit any publicly-trusted certificate — the exact
            // opposite of what this rule is for.
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(anchor);

            // Intermediates the enroller filed travel with the leaf (Encryption Module ADR 0002 files the bundle), so they are
            // offered here rather than fetched — this path must not reach the network.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var extra = new X509Certificate2Collection();
            extra.ImportFromPem(chainPem);
            chain.ChainPolicy.ExtraStore.AddRange(extra);

            return chain.Build(leaf);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or ArgumentException or FormatException)
        {
            return false;
        }
    }
}
