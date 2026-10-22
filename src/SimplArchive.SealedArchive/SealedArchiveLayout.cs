namespace SimplArchive.SealedArchive;

/// <summary>
/// Where each part of a <c>.simplarchive</c> lives in its zip (ADR 0961). Only the cover, its signature and the
/// published signing keys are readable without a key; everything else is sealed.
/// </summary>
public static class SealedArchiveLayout
{
    /// <summary>The file name extension of a sealed archive.</summary>
    public const string Extension = ".simplarchive";

    /// <summary>What <see cref="ArchiveCover.Format"/> says, so a reader knows the file before it trusts it.</summary>
    public const string Format = "simplarchive";

    /// <summary>The container's version. A reader refuses a newer one by name rather than misreading it.</summary>
    public const int FormatVersion = 1;

    /// <summary>The cover (JSON, plain): name, date, source, recipient labels, and the hashes that bind the rest.</summary>
    public const string Cover = "cover.json";

    /// <summary>The installation's detached CMS signature over the cover's exact bytes, its certificate embedded.</summary>
    public const string CoverSignature = "cover.p7s";

    /// <summary>The installation's signing keys at export time, newest first (PEM certificates, plain).</summary>
    public const string SigningKeys = "signing-keys.pem";

    /// <summary>The payload key, sealed in one CMS envelope to every recipient's certificate.</summary>
    public const string KeyEnvelope = "key.p7m";

    /// <summary>The table of contents (sealed): each payload path, its entry, its SHA-256 and its length.</summary>
    public const string Contents = "payload/contents";

    /// <summary>The entry that holds the payload file at <paramref name="index"/>; the names say nothing of the content.</summary>
    public static string Entry(int index) => $"payload/{index}";
}
