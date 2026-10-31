namespace Alcifra.Api.PasswordStores;

// The password store's wire shapes (ADR 0964). Plain mutable classes with parameterless constructors, as every resource
// here: responses negotiate to XML as well as JSON (ADR 0190).

/// <summary>The caller's password stores: My Passwords (when it exists) first, then every shared store they may see.</summary>
public class PasswordStoreListResource : HypermediaResource
{
    public List<PasswordStoreSummary> Stores { get; set; } = [];
}

/// <summary>One store in the list; its <c>self</c> leads to the store's contents.</summary>
public class PasswordStoreSummary : HypermediaResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>What a list shows: the name qualified by the parent's, <c>Parent / Name</c> (ADR 0619), so two stores
    /// of one name — two users' My Passwords — can be told apart. <see cref="Name"/> stays the document's own name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>True for the caller's own My Passwords.</summary>
    public bool IsPersonal { get; set; }
}

/// <summary>A store or a folder in one, with its direct contents (folders first, then by name).</summary>
public class PasswordFolderResource : HypermediaResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A store's name as the store list shows it, <c>Parent / Name</c> (ADR 0619); a folder's own name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary><c>store</c> or <c>folder</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The store this folder belongs to (its own id for a store).</summary>
    public Guid StoreId { get; set; }

    public bool IsPersonal { get; set; }

    /// <summary>The ETag of the store or folder, also in the response header.</summary>
    public string ETag { get; set; } = string.Empty;

    /// <summary>
    /// True when some entry of the STORE is not sealed to exactly its readers' current certificates. Always present; the
    /// <c>reseal</c> rel rides beside it only for a caller who may re-seal (edit rights on the store).
    /// </summary>
    public bool NeedsReseal { get; set; }

    /// <summary>
    /// True when an entry of the store is still sealed to a REVOKED certificate (ADR 0964): re-seal at once, change the
    /// secrets, and a phone drops its offline copy of the store.
    /// </summary>
    public bool UrgentReseal { get; set; }

    /// <summary>How many certificates the store's entries are sealed to, all readers together: a personal store with one
    /// is one lost key away from losing its entries, so the app recommends a spare.</summary>
    public int ReaderCertificateCount { get; set; }

    /// <summary>
    /// Why the <c>password-entries</c> rel is withheld from a caller who may create here (ADR 0982), so a client can say
    /// so instead of merely greying the action out. The REL decides availability; this only explains its absence.
    /// <c>enrolment-required</c>: you read the store but hold no enrolled hardware key an entry could be sealed to.
    /// <c>not-a-reader</c>: you may create here but are not in the store's effective audience (ADR 0983; the value keeps
    /// its released name), so nothing would be sealed to you. <c>no-reader-certificates</c>: a service account, and none of the store's readers
    /// holds a key. Null when entries may be created, and when the caller may not create here at all.
    /// </summary>
    public string? NewEntryUnavailableReason { get; set; }

    public List<PasswordItemResource> Items { get; set; } = [];
}

/// <summary>One row of a folder: a folder or an entry, with the flags that gate its actions (ADR 0719).</summary>
public class PasswordItemResource : HypermediaResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary><c>folder</c> or <c>entry</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The ETag a rename, move or delete of this row sends as <c>If-Match</c>.</summary>
    public string ETag { get; set; } = string.Empty;

    public bool CanEdit { get; set; }

    /// <summary>Whether a PUT of the name to the <c>document</c> rel is allowed.</summary>
    public bool CanRename { get; set; }

    public bool CanDelete { get; set; }

    public bool CanMove { get; set; }

    /// <summary>The entry's site (ADR 0973) for an autofill sheet built from a listing; null for a folder or an entry
    /// no client has re-filed since the field existed.</summary>
    public string? Site { get; set; }

    /// <summary>The entry's user name (ADR 0973), or null.</summary>
    public string? UserName { get; set; }

    /// <summary>The entry's Android package name (ADR 0973), or null.</summary>
    public string? App { get; set; }

    /// <summary>Whether the entry carries a verification code (ADR 1013): THAT a one-time code exists, never anything of
    /// its secret. False for a folder, and for an entry no client has filed since the field existed.</summary>
    public bool HasCode { get; set; }
}

/// <summary>One entry: what opening, editing and re-sealing it needs. Its ETag is the <c>If-Match</c> of a new version.</summary>
public class PasswordEntryResource : HypermediaResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid StoreId { get; set; }

    public Guid ParentId { get; set; }

    public Guid? CurrentVersionId { get; set; }

    public int? VersionNumber { get; set; }

    /// <summary>The ETag of the document, also in the response header.</summary>
    public string ETag { get; set; } = string.Empty;

    /// <summary>True when this entry is not sealed to exactly its store's readers' current certificates.</summary>
    public bool NeedsReseal { get; set; }

    /// <summary>
    /// The server's "Exposed since" (ADR 0964), <c>yyyy-MM-dd</c>: a certificate the entry was sealed to was revoked then.
    /// Cleared only by filing a new password. Null when the entry is not exposed.
    /// </summary>
    public string? ExposedSince { get; set; }

    public bool CanEdit { get; set; }

    /// <summary>Whether a PUT of the name to the <c>document</c> rel is allowed.</summary>
    public bool CanRename { get; set; }

    public bool CanDelete { get; set; }

    public bool CanMove { get; set; }

    /// <summary>
    /// The site the entry belongs to (ADR 0973), derived by the client from the URL inside the sealed content: what
    /// autofill matches a browser's domain against without opening the entry. Null until a client has filed a version
    /// since the field existed.
    /// </summary>
    public string? Site { get; set; }

    /// <summary>The user name the entry fills (ADR 0973), shown in the autofill sheet before the key is used.</summary>
    public string? UserName { get; set; }

    /// <summary>The Android package name the entry also fills (ADR 0973), where the user set one.</summary>
    public string? App { get; set; }

    /// <summary>Whether the entry carries a verification code (ADR 1013): THAT a one-time code exists, never anything of
    /// its secret. False for a folder, and for an entry no client has filed since the field existed.</summary>
    public bool HasCode { get; set; }
}
public class PasswordStoreRecipientsResource : HypermediaResource
{
    public Guid StoreId { get; set; }

    public List<PasswordStoreRecipient> Certificates { get; set; } = [];

    /// <summary>Readers who may read the store but hold no attested certificate: nothing can be sealed to them yet.</summary>
    public List<PasswordStoreReaderSummary> ReadersWithoutCertificate { get; set; } = [];
}

public class PasswordStoreRecipient
{
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The end-entity certificate, PEM.</summary>
    public string CertificatePem { get; set; } = string.Empty;
}

public class PasswordStoreReaderSummary
{
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>The entries of a store a member with edit rights should seal again, each with what its new version needs.</summary>
public class PasswordStoreResealResource : HypermediaResource
{
    public Guid StoreId { get; set; }

    public List<PasswordEntryResource> Entries { get; set; } = [];
}

/// <summary>The body of a new folder or entry: its name, in the clear.</summary>
public class CreatePasswordItemRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>A new entry, with its first version pending: PUT the sealed bytes to the upload URL, then PUT the version.</summary>
public class CreatedPasswordEntryResource : HypermediaResource
{
    public Guid Id { get; set; }

    public Guid VersionId { get; set; }

    public Uri UploadUrl { get; set; } = null!;

    /// <summary>Present only on a tenant that encrypts at rest: the client encrypts the upload as for any version (ADR 0818).</summary>
    public Controllers.DocumentVersionsController.UploadEncryption? Encryption { get; set; }
}

/// <summary>
/// Every folder and entry beneath a store or folder, one cursor-paginated page at a time (ADR 0207) — the flat
/// tree apass completes and searches over, so one listing answers what the per-folder listing answers in N
/// requests. Names are in the clear (ADR 0964): nothing here needs the key.
/// </summary>
public class PasswordDescendantsResource : HypermediaResource
{
    public List<PasswordDescendantResource> Items { get; set; } = [];
}

/// <summary>One row of the flat listing. <see cref="ParentId"/> lets a client build each row's path from the set it
/// holds; its <c>self</c> rel is the row's own resource, so a client follows it rather than composing a URL (ADR 0543).</summary>
public class PasswordDescendantResource : HypermediaResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary><c>folder</c> or <c>entry</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The containing folder — always present for a descendant, never the store itself.</summary>
    public Guid ParentId { get; set; }

    /// <summary>The entry's autofill metadata (ADR 0973), in the clear and null when it has none: which site and user
    /// name it fills. An autofill client matches on these WITHOUT opening anything, which is the point of the index.</summary>
    public string? Site { get; set; }

    /// <inheritdoc cref="Site"/>
    public string? UserName { get; set; }

    /// <inheritdoc cref="Site"/>
    public string? App { get; set; }

    /// <summary>Whether the entry carries a verification code (ADR 1013): THAT a one-time code exists, never anything of
    /// its secret. False for a folder, and for an entry no client has filed since the field existed.</summary>
    public bool HasCode { get; set; }
}
