using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Audience;

/// <summary>
/// The audience of a sealed item (ADR 0983): who is meant to decrypt it, who may change that, who it is sealed to now,
/// and — for a caller who may change it — the readers who could be added.
/// </summary>
/// <remarks>
/// One <c>self</c> rel serves GET and PUT (ADR 0719); whether this caller may PUT is the capability flags
/// <see cref="CanManage"/> (an effective manager) and <see cref="CanAppointManager"/> (a tenant administrator while no
/// manager is left, who may then only appoint one). The ETag is the
/// DECLARING DOCUMENT's: the audience is written under it, so a second manager's stale list is a 412.
/// </remarks>
public class ContentAudienceResource : HypermediaResource
{
    /// <summary>The document that declares the audience: a Password Store or a Sealed document.</summary>
    public Guid DocumentId { get; set; }

    /// <summary><c>password-store</c> or <c>sealed-document</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>In a personal space: the owner is the whole audience, and nothing can be changed.</summary>
    public bool IsPersonal { get; set; }

    /// <summary>The declaring document's ETag, the precondition of a <c>PUT</c>.</summary>
    public string ETag { get; set; } = string.Empty;

    /// <summary>The caller is an effective manager and may change members and managers.</summary>
    public bool CanManage { get; set; }

    /// <summary>No effective manager is left and the caller is a tenant administrator: they may appoint one.</summary>
    public bool CanAppointManager { get; set; }

    /// <summary>The caller is in the effective audience.</summary>
    public bool IsMember { get; set; }

    /// <summary>The declared rows, users first, as an editor shows them (none in a personal space).</summary>
    public List<ContentAudienceDeclaredMember> Members { get; set; } = [];

    /// <summary>The effective audience with the certificates its content is sealed to: what a client seals with.</summary>
    public List<ContentAudienceRecipient> Recipients { get; set; } = [];

    /// <summary>Readers not declared yet, offered to a caller who may change the audience; empty for anyone else.</summary>
    public List<ContentAudienceCandidate> Candidates { get; set; } = [];
}

/// <summary>One declared row: a user or a group.</summary>
public class ContentAudienceDeclaredMember
{
    public Guid? UserId { get; set; }

    public Guid? GroupId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public bool IsManager { get; set; }

    /// <summary>
    /// For a user: whether they are in the EFFECTIVE audience (active and a reader). False shows a declared member who
    /// has lost read access or was deactivated. For a group: whether any of its members is.
    /// </summary>
    public bool InEffect { get; set; }
}

/// <summary>One effective member's certificate, or a member who holds none yet.</summary>
public class ContentAudienceRecipient
{
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public bool IsManager { get; set; }

    /// <summary>The member's attested certificates (PEM); empty while they have enrolled none.</summary>
    public List<string> CertificatePems { get; set; } = [];
}

/// <summary>A reader who could be added: a user, or a group with at least one reader.</summary>
public class ContentAudienceCandidate
{
    public Guid? UserId { get; set; }

    public Guid? GroupId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>The body of a <c>PUT</c>: the whole declared audience, replacing what is stored.</summary>
public class ContentAudienceWrite
{
    public List<ContentAudienceWriteMember> Members { get; set; } = [];
}

/// <summary>One row of a <c>PUT</c>: exactly one of user and group, and a manager only ever a user.</summary>
public class ContentAudienceWriteMember
{
    public Guid? UserId { get; set; }

    public Guid? GroupId { get; set; }

    public bool IsManager { get; set; }
}
