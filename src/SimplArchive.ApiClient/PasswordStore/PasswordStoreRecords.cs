using System.Globalization;
using System.Text.Json;

namespace SimplArchive.ApiClient.PasswordStore;

/// <summary>A store in the caller's list (ADR 0964); <see cref="Href"/> leads to its contents.</summary>
public sealed record PasswordStoreInfo(Guid Id, string Name, bool IsPersonal, string Href);

/// <summary>A row of a store or folder: a folder or an entry, with the flags that gate its actions (ADR 0719).</summary>
/// <param name="Site">An entry's autofill site (ADR 0973), the URL's host: what a fill matches without opening it.
/// Null for a folder, and for an entry no client has filed since the field existed.</param>
/// <param name="UserName">An entry's user name (ADR 0973), shown before the key is used; null where none is known.</param>
/// <param name="App">The Android package name an entry also fills (ADR 0973), or null.</param>
public sealed record PasswordItemInfo(
    Guid Id, string Name, bool IsFolder, string ETag, bool CanEdit, bool CanRename, bool CanDelete, bool CanMove, LinkMap Links,
    string? Site = null, string? UserName = null, string? App = null)
{
    /// <summary>The row's own resource: a folder's contents, or an entry.</summary>
    public string Href => Links.Href("self") ?? throw new InvalidOperationException($"'{Name}' advertised no 'self' rel (ADR 0543).");
}

/// <summary>A store or a folder in one, with its contents and its store's sealing state.</summary>
/// <param name="NeedsReseal">Some entry of the store is not sealed to exactly its readers' current certificates.</param>
/// <param name="UrgentReseal">Some entry is still sealed to a REVOKED certificate: re-seal and change it now; a phone drops
/// its offline copy of the store.</param>
/// <param name="ReaderCertificateCount">How many certificates the store's entries are sealed to, all readers together.</param>
public sealed record PasswordFolderInfo(
    Guid Id, string Name, bool IsStore, Guid StoreId, bool IsPersonal, bool NeedsReseal, bool UrgentReseal, int ReaderCertificateCount,
    string ETag, IReadOnlyList<PasswordItemInfo> Items, LinkMap Links)
{
    /// <summary>Whether entries and folders may be created here (the create rels are advertised).</summary>
    public bool CanCreate => Links.Has("password-entries");

    /// <summary>Whether the caller is the one to re-seal (the <c>reseal</c> rel rides only for a member with edit rights).</summary>
    public bool CanReseal => Links.Has("reseal");

    /// <summary>
    /// My Passwords sealed to ONE certificate is one lost key away from losing every entry: the app recommends enrolling a
    /// spare key (owner decision 2026-10-23).
    /// </summary>
    public bool RecommendsSpareKey => IsStore && IsPersonal && ReaderCertificateCount == 1;
}

/// <summary>One entry: what opening, editing and re-sealing it needs.</summary>
/// <param name="ETag">The If-Match of its next version, a rename, a move or a delete.</param>
/// <param name="ExposedSince">The day a certificate it was sealed to was revoked, or null; cleared only by a new password.</param>
public sealed record PasswordEntryInfo(
    Guid Id, string Name, Guid StoreId, Guid ParentId, int? VersionNumber, string ETag, bool NeedsReseal, DateOnly? ExposedSince,
    bool CanEdit, bool CanRename, bool CanDelete, bool CanMove, LinkMap Links,
    string? Site = null, string? UserName = null, string? App = null)
{
    /// <summary>Whether it has content yet: an entry whose first upload never finished has none.</summary>
    public bool HasContent => Links.Has("content");

    internal static PasswordEntryInfo From(JsonElement json) => new(
        json.GetProperty("id").GetGuid(),
        json.GetProperty("name").GetString() ?? string.Empty,
        json.GetProperty("storeId").GetGuid(),
        json.GetProperty("parentId").GetGuid(),
        json.TryGetProperty("versionNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null,
        json.GetProperty("eTag").GetString() ?? string.Empty,
        json.GetProperty("needsReseal").GetBoolean(),
        json.TryGetProperty("exposedSince", out var e) && e.GetString() is { Length: > 0 } day
            ? DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null,
        json.GetProperty("canEdit").GetBoolean(),
        json.GetProperty("canRename").GetBoolean(),
        json.GetProperty("canDelete").GetBoolean(),
        json.GetProperty("canMove").GetBoolean(),
        LinkMap.From(json) ?? LinkMap.Empty,
        ApiWire.StrOrNull(json, "site"),
        ApiWire.StrOrNull(json, "userName"),
        ApiWire.StrOrNull(json, "app"));
}

/// <summary>An entry opened with the reader's key: its resource as read, and its content.</summary>
public sealed record OpenedPasswordEntry(PasswordEntryInfo Entry, PasswordEntryContent Content);

/// <summary>A certificate a store's entries are sealed to, and whose it is.</summary>
public sealed record PasswordStoreRecipientInfo(Guid UserId, string DisplayName, string CertificatePem);

/// <summary>Who a store's entries must be sealed to, and the readers nothing can be sealed to yet.</summary>
public sealed record PasswordStoreRecipients(
    IReadOnlyList<PasswordStoreRecipientInfo> Certificates, IReadOnlyList<(Guid UserId, string DisplayName)> ReadersWithoutCertificate);

/// <summary>What a re-seal did: entries sealed again, entries skipped because somebody changed them meanwhile (412), and
/// entries this key could not open, with why.</summary>
public sealed record PasswordResealOutcome(
    IReadOnlyList<string> Resealed, IReadOnlyList<string> SkippedChanged, IReadOnlyList<(string Name, string Reason)> Failed);

/// <summary>Somebody changed the entry since it was read: reload it and decide again (ADR 0794's real UI case).</summary>
public sealed class PasswordEntryChangedException(string name)
    : Exception($"'{name}' was changed by somebody else since it was opened; reload it.");

/// <summary>The store's readers hold no certificate a secret could be sealed to, so nothing can be saved there yet.</summary>
public sealed class PasswordStoreHasNoRecipientsException()
    : Exception("Nobody who reads this password store has an enrolled hardware key yet, so nothing can be sealed to it.");

/// <summary>One row of a store's flat descendants listing: its id and name, folder or entry, its parent, and its own
/// resource address (the <c>self</c> rel the server advertised — followed, never composed, ADR 0543).</summary>
public sealed record PasswordDescendantInfo(
    Guid Id, string Name, bool IsFolder, Guid ParentId, string Href,
    string? Site = null, string? UserName = null, string? App = null);
