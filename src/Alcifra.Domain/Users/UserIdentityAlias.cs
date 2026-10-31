using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Users;

/// <summary>
/// A further address a user is known by, on one of the tenant's identity domains (ADR 1015).
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not sign in.</b> <c>User.Email</c> stays the one address an account signs in with and is written to. An
/// alias answers a different question, "who does this address name": Strict certificate binding accepts a certificate
/// naming an alias, and a document that represents an address (a certificate's holder) resolves to the user. That is
/// what lets an account change its address while the certificates on its hardware keys keep the old one.
/// </para>
/// <para>
/// Unique per tenant, and never another user's address in the same tenant (both directions, enforced in
/// <c>SaveChanges</c>), so an address names at most one person.
/// </para>
/// </remarks>
public class UserIdentityAlias : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>As it was entered, for display. Setting it maintains <see cref="NormalizedAddress"/>.</summary>
    public required string Address
    {
        get => _address;
        set
        {
            _address = value;
            NormalizedAddress = value.Trim().ToUpperInvariant();
        }
    }

    private string _address = string.Empty;

    /// <summary>The lookup key; never set this directly.</summary>
    public string NormalizedAddress { get; set; } = string.Empty;

    /// <summary>
    /// When the address was confirmed as this user's. An administrator's addition is confirmed at once (vouched for
    /// and audited); an unconfirmed alias names nobody.
    /// </summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
