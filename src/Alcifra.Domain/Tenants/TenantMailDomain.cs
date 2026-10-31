using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Tenants;

/// <summary>A domain this tenant has registered: one its accounts' addresses live on, one it receives mail for, or both
/// (ADR 0628; the two roles: ADR 1015).</summary>
/// <remarks>
/// <para>
/// The envelope recipient resolves in two steps — the <b>domain</b> identifies the tenant, the <b>local part</b>
/// the user. So a <b>receiving</b> domain belongs to exactly one tenant, and that is what makes it the isolation
/// boundary here, the same role the tenant query filter plays everywhere else: its unique index is deliberately
/// <b>not</b> scoped to <c>TenantId</c>. An <b>identity-only</b> domain routes nothing, so several tenants may
/// hold it (a person's work address, one token, two accounts; ADR 1015); a tenant still holds a domain once.
/// </para>
/// <para>
/// A tenant may hold several — a real organisation has <c>example.com</c> and <c>example.de</c> — which is why
/// this is its own table rather than a column on <c>Tenant</c>.
/// </para>
/// <para>
/// <b>Tenant-scoped, but resolved before the tenant is known.</b> Every lookup that turns a recipient into a
/// tenant necessarily runs with no tenant set, so it must use
/// <c>IgnoreQueryFilters(["TenantFilter"])</c> — the same rule the login and client-id lookups follow. Left
/// unfiltered it silently matches zero rows and every message is refused as an unknown recipient.
/// </para>
/// </remarks>
public class TenantMailDomain : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>As the administrator typed it, for display.</summary>
    /// <remarks>
    /// Setting this populates <see cref="NormalizedDomain"/>, exactly as <c>User.Email</c> populates
    /// <c>NormalizedEmail</c>: always set <see cref="Domain"/>, never the normalized form, and look up by the
    /// normalized one. A mail domain is case-insensitive, so the raw value cannot be the key.
    /// </remarks>
    public required string Domain
    {
        get => _domain;
        set
        {
            _domain = value;
            NormalizedDomain = value.Trim().TrimEnd('.').ToUpperInvariant();
        }
    }

    private string _domain = string.Empty;

    /// <summary>Upper-cased, trimmed of the trailing root dot. The lookup key; never set this directly.</summary>
    public string NormalizedDomain { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The value the administrator publishes as a <c>TXT</c> record to prove they control the domain (#667).
    /// </summary>
    /// <remarks>
    /// Stored rather than minted per request, and that is the whole mechanism: a challenge the verifier
    /// regenerates is not a challenge, because what the administrator was shown and what the lookup expects
    /// would differ every time. Null on a domain that never needed one — see <see cref="VerifiedAt"/>.
    /// </remarks>
    public string? VerificationToken { get; set; }

    /// <summary>
    /// When ownership was established, or <see langword="null"/> while it has not been.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Delivery accepts only a verified domain</b>, and so does the MTA's own virtual-domain query — both,
    /// deliberately. If only the app checked, Postfix would accept an unverified recipient at <c>RCPT</c> and
    /// the refusal would arrive later as a bounce; refusing up front is what ADR 0628 asks for.
    /// </para>
    /// <para>
    /// A domain declared by the stack's own CONFIGURATION arrives verified with no token and no lookup. An
    /// operator writing it into the configuration that also creates the tenant and its administrator is the
    /// assertion of ownership — and it is the only workable answer for the demo and kiosk stacks, which have
    /// no zone to publish a record into.
    /// </para>
    /// </remarks>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>When a verification check last ran, successful or not — so the UI can say how fresh it is.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>
    /// Account addresses live on this domain: a user's identity aliases may be addressed here, and Strict certificate
    /// binding accepts a certificate naming one (ADR 1015).
    /// </summary>
    public bool IsIdentity { get; set; } = true;

    /// <summary>
    /// Mail to this domain is routed here and files into the archive. Only a receiving domain is unique across the
    /// installation, and only a receiving domain is accepted by delivery and the MTA (ADR 1015).
    /// </summary>
    public bool IsReceiving { get; set; } = true;

    /// <summary>Changing a domain's roles is an in-place edit, so two administrators cannot revert each other.</summary>
    public Guid ConcurrencyToken { get; set; }

}
