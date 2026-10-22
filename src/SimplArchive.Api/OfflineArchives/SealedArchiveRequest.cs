namespace SimplArchive.Api.OfflineArchives;

/// <summary>Somebody outside the tenant an archive is sealed to, by an uploaded certificate (ADR 0961).</summary>
public sealed record ExternalArchiveRecipient(string Label, string CertificatePem);

/// <summary>
/// What a road copy holds and whom it is for (ADR 0961). The export job (#1696) fills it from the dialog; the archive
/// is always for the exporter too, and always sealed to the tenant's recovery certificate (ADR 0960).
/// </summary>
/// <param name="AnchorId">The chosen folder or document; everything below it the exporter may read travels.</param>
/// <param name="Name">The cover's name; null names it after the anchor.</param>
/// <param name="IncludePreviews">Whether renditions travel too (rendered where missing).</param>
/// <param name="MemberUserIds">Tenant members it is also for, sealed to each one's certificate set (ADR 0842).</param>
/// <param name="ExternalRecipients">People outside the tenant, by uploaded certificate.</param>
/// <param name="ApiRoot">The API root a later write-back goes to (the source's identity, with the tenant).</param>
public sealed record SealedArchiveRequest(
    Guid AnchorId,
    string? Name,
    bool IncludePreviews,
    IReadOnlyList<Guid> MemberUserIds,
    IReadOnlyList<ExternalArchiveRecipient> ExternalRecipients,
    string ApiRoot);

/// <summary>What was written: the cover, and the counts the job's audit line names (#1696).</summary>
public sealed record SealedArchiveWritten(SimplArchive.SealedArchive.ArchiveCover Cover, int Documents, int Versions, int Previews);
