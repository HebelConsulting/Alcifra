using System.Security.Cryptography;

namespace SimplArchive.ModuleAbi;

/// <summary>
/// A licence for the SimplArchive Mobile app (core ADR 0954; SimplArchiveMobile ADR 0019): for installs outside the app
/// stores and for organisations buying for their staff. The vendor signs it with the same key ring and the same signature
/// as a <see cref="TenantLicense"/>; the app verifies it OFFLINE with the vendor keys it ships, and it unlocks the app's
/// <see cref="Level"/> for the profiles whose user name is in one of its <see cref="Domains"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bound to mail domains, not counted seats</b> (owner, 2026-10-21): an offline code cannot count the devices it is
/// pasted on, so seats are a matter of the contract; a code passed outside the customer unlocks nothing of the
/// outsider's, because their user names are not in the customer's domains.
/// </para>
/// <para>
/// <b>Its own payload kind.</b> <see cref="SignedPayload"/> starts with <see cref="Kind"/>, so a tenant licence's
/// signature can never be read as a mobile licence's, though one key signs both.
/// </para>
/// </remarks>
/// <param name="Level">The app level it unlocks: <see cref="Standard"/> or <see cref="Pro"/>.</param>
/// <param name="Domains">The customer's mail domains, such as <c>kristall.ch</c>; compared case-insensitively.</param>
/// <param name="Licensee">Who the licence was issued to, as the app shows it (the organisation's name).</param>
/// <param name="ValidUntil">The last day it unlocks the app, INCLUSIVE.</param>
/// <param name="Signature">Base64 ECDsa P-256/SHA-256 signature over <see cref="SignedPayload"/>.</param>
public sealed record MobileLicense(
    string Level,
    IReadOnlyList<string> Domains,
    string Licensee,
    DateOnly ValidUntil,
    string Signature)
{
    /// <summary>The first line of the signed payload: what kind of licence this is.</summary>
    /// <remarks>Wire, so fixed whatever the brand (core ADR 0987); this leaf references nothing, so it is spelled here.</remarks>
    public const string Kind = "alcifra-mobile";

    /// <summary>The kind before the rename (core #1117 phase 4b): a licence signed with it still verifies until core v0.57.0.</summary>
    /// <remarks>Retirement trigger (core ADR 0885): removed with the core's <c>WireNames.Legacy</c>; licences are re-issued.</remarks>
    public const string LegacyKind = "simplarchive-mobile";

    /// <summary>The level for the phone layout on any device.</summary>
    public const string Standard = "standard";

    /// <summary>The level that adds the tablet layouts.</summary>
    public const string Pro = "pro";

    /// <summary>
    /// The exact string the vendor signs and the app verifies: <see cref="Kind"/>, the level, the domains lower-cased,
    /// sorted and comma-joined, the licensee, and the date as <c>yyyy-MM-dd</c>, newline-joined. Changing it invalidates
    /// every issued mobile licence.
    /// </summary>
    public string SignedPayload() => PayloadOf(Kind);

    private string PayloadOf(string kind) =>
        $"{kind}\n{Level}\n{string.Join(",", Domains.Select(d => d.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal))}\n"
        + $"{Licensee}\n{ValidUntil:yyyy-MM-dd}";

    /// <summary>Signs the claims with the vendor's private key (the Licensing module's half).</summary>
    public MobileLicense Sign(ECDsa privateKey) => this with { Signature = SignedClaims.Sign(privateKey, SignedPayload()) };

    /// <summary>True when <paramref name="publicKey"/> signed exactly these claims.</summary>
    /// <remarks>A licence signed under <see cref="LegacyKind"/> verifies too, until that is retired.</remarks>
    public bool VerifiedBy(ECDsa publicKey) =>
        SignedClaims.Verify(publicKey, SignedPayload(), Signature)
        || SignedClaims.Verify(publicKey, PayloadOf(LegacyKind), Signature);

    /// <summary>Whether it still unlocks on <paramref name="day"/> (the last day included).</summary>
    public bool IsValidOn(DateOnly day) => day <= ValidUntil;

    /// <summary>Whether it unlocks a profile signed in as <paramref name="userName"/>: an e-mail address in one of the
    /// licence's domains, exactly that domain (a subdomain is a different domain).</summary>
    public bool Unlocks(string? userName) =>
        userName?.Trim() is { } name && name.LastIndexOf('@') is var at and > 0
        && Domains.Any(d => string.Equals(name[(at + 1)..], d.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the level is Pro (the tablet layouts, ADR 0019).</summary>
    public bool IsPro => string.Equals(Level, Pro, StringComparison.Ordinal);
}
