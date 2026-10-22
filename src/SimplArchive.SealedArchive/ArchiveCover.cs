using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplArchive.SealedArchive;

/// <summary>Who a recipient of the payload is (ADR 0961).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArchiveRecipientKind>))]
public enum ArchiveRecipientKind
{
    /// <summary>The person who exported it.</summary>
    Exporter,

    /// <summary>A member of the tenant the exporter chose.</summary>
    Member,

    /// <summary>Somebody outside the tenant, addressed by an uploaded certificate.</summary>
    External,

    /// <summary>The tenant's recovery certificate (escrow, ADR 0960): every archive is sealed to it.</summary>
    Recovery,
}

/// <summary>Where an archive was exported: the API root a later write-back goes to, and the tenant.</summary>
public sealed record ArchiveSource(string ApiRoot, Guid TenantId, string TenantName, string ServerVersion);

/// <summary>One certificate the payload key is sealed to, as the cover names it: enough for "which card opens this".</summary>
public sealed record ArchiveRecipientLabel(string Label, ArchiveRecipientKind Kind, string Thumbprint, DateTimeOffset NotAfter);

/// <summary>
/// The cover of a <c>.simplarchive</c> (ADR 0961): plain and signed, so the server list, the banner and "which card
/// opens this" need no key. Nothing of the content is here except the hashes that bind it.
/// </summary>
/// <param name="ContentsSha256">SHA-256 of the sealed table of contents' plaintext, which names every payload file's hash.</param>
/// <param name="KeyEnvelopeSha256">SHA-256 of the key envelope, so a recipient cannot be added or swapped unseen.</param>
/// <param name="SigningKeysSha256">SHA-256 of the embedded signing keys file.</param>
public sealed record ArchiveCover(
    string Format,
    int FormatVersion,
    string Name,
    DateTimeOffset ExportedAt,
    ArchiveSource Source,
    IReadOnlyList<ArchiveRecipientLabel> Recipients,
    string ContentsSha256,
    string KeyEnvelopeSha256,
    string SigningKeysSha256)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    internal static ArchiveCover? FromBytes(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ArchiveCover>(bytes, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One file of the sealed payload, as the table of contents lists it.</summary>
internal sealed record ContentsEntry(string Path, string Entry, string Sha256, long Length);
