using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Users;

/// <summary>What kind of credential a protocol device row is (#1970).</summary>
public enum ProtocolDeviceKind
{
    /// <summary>A device the user enrolled: its own <c>&lt;key&gt;.&lt;secret&gt;</c> password and its own switches.</summary>
    Enrolled = 0,

    /// <summary>The WebDAV/calendar password from before per-device enrolment (Files, Calendars, Contacts).</summary>
    LegacyDav = 1,

    /// <summary>The IMAP password from before per-device enrolment (Mail); its encryption behaves as IMAP always did.</summary>
    LegacyImap = 2,
}

/// <summary>
/// One device a user lets reach the protocols (IMAP, WebDAV, CalDAV, CardDAV) with a password of its own (#1970).
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by its key, verified by one hash.</b> An enrolled device's password is <c>&lt;key&gt;.&lt;secret&gt;</c>: the
/// key (public, unique installation-wide) names the row, so a login verifies exactly one deliberately slow hash rather
/// than every device of the user, on every DAV request. Legacy rows have no key; a password without one is tried only
/// against the user's legacy rows.
/// </para>
/// <para>
/// <b>A switch that is off refuses the password as a wrong one would</b>, so a prober learns nothing; the refusal is
/// logged as a Warning naming the switch (ADR 0626).
/// </para>
/// </remarks>
public class ProtocolDevice : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The device's name as the user gave it; unique per user.</summary>
    public required string Name { get; set; }

    /// <summary>The public first part of an enrolled device's password; null for a legacy row.</summary>
    public string? CredentialKey { get; set; }

    /// <summary>The hash of the secret (the password's second part, or a legacy row's whole password).</summary>
    public required string SecretHash { get; set; }

    public ProtocolDeviceKind Kind { get; set; }

    public bool MailEnabled { get; set; }

    public bool FilesEnabled { get; set; }

    public bool CalendarsEnabled { get; set; }

    public bool ContactsEnabled { get; set; }

    /// <summary>Whether IMAP seals this device's mail. Disregarded for a <see cref="ProtocolDeviceKind.LegacyImap"/> row,
    /// which behaves as IMAP always did, and under the Module's Strict mode, which always seals.</summary>
    public bool EncryptMail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a login last used it; moved at most hourly by a claim that leaves the token (it drifts on its own,
    /// ADR 0878).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
