using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Certificates;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.OfflineArchives;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.SealedArchive;

namespace SimplArchive.Api.OfflineArchives;

/// <summary>
/// Writes a road copy (ADR 0961, #1694): the export layout of ADR 0347 with every version the exporter may read, the
/// sync preparation and, opted in, the previews, sealed to the exporter, the chosen members, external recipients and
/// ALWAYS the tenant's recovery certificate (ADR 0960), under a cover the installation signs.
/// </summary>
/// <remarks>
/// <para>
/// <b>One producer of the layout</b>: the payload is the regular exporter's output, given the exporter as its audience
/// (what they may read, no permissions), so a road copy and an export cannot drift. This class adds only what a road
/// copy has beyond it, and seals the result with <see cref="SealedArchiveWriter"/>, the one container implementation.
/// </para>
/// <para>
/// <b>Refusals come first and by name</b>, before anything is read: a tenant that refuses plaintext doors (slice 1),
/// no recovery certificate, no signing key, a recipient with no certificate. The job (#1696) owns the right, the
/// release by card, the download and the audit; this writes to the stream it is handed and files nothing.
/// </para>
/// </remarks>
public sealed class SealedArchiveExporter(
    SimplArchiveDbContext dbContext,
    RepositoryExporter exporter,
    DocumentAccessService access,
    StrictEnvelopeDelivery strictTier,
    ReaderCertificateSet readerCertificates,
    ArchiveSigningKey signingKey,
    IRecoveryCertificateSource recovery,
    IDocumentPreviewService previews,
    IObjectStorageClient storage,
    ICurrentUserAccessor currentUser,
    ICurrentTenantAccessor currentTenant,
    ILogger<SealedArchiveExporter> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SealedArchiveWritten> ExportAsync(SealedArchiveRequest request, Stream output, CancellationToken cancellationToken)
    {
        if (await strictTier.RefusesPlaintextDoorsAsync(cancellationToken))
        {
            throw new OfflineArchiveNotOnThisTenantException();
        }

        var recipients = await RecipientsAsync(request, cancellationToken);
        var signing = signingKey.Require();

        var anchor = await dbContext.Documents.Where(d => d.Id == request.AnchorId).Select(d => new { d.Id, d.Name }).SingleOrDefaultAsync(cancellationToken);
        if (anchor is null || !(await access.GetCallerRightsAsync(anchor.Id, cancellationToken)).CanSee)
        {
            throw DocumentNotFoundException.NotFound();
        }

        var tenantId = currentTenant.TenantId ?? throw new InvalidOperationException("An offline archive needs a current tenant.");
        var tenantName = await dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        var exportedAt = DateTimeOffset.UtcNow;

        // The payload in the export layout, through the one producer of it, into a file that is gone when we are done.
        await using var payload = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        await exporter.ExportAsync(anchor.Id, new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.EveryReadableForCaller, null),
            includePermissions: false, payload, new OfflineExportAudience(currentUser.UserId, access.GetCallerRightsForManyAsync), cancellationToken);
        payload.Position = 0;

        await using var writer = new SealedArchiveWriter(output);
        using var inner = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in inner.Entries)
        {
            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            files[entry.FullName] = buffer.ToArray();
            await writer.AddAsync(entry.FullName, files[entry.FullName], cancellationToken);
        }

        var documentIds = Rows(files, "tree/documents.jsonl").Select(r => r.GetProperty("id").GetGuid()).ToList();
        var versionRows = Rows(files, "tree/versions.jsonl").ToList();
        await WriteSyncAsync(writer, request.ApiRoot, tenantId, exportedAt, documentIds, versionRows, cancellationToken);
        var previewCount = request.IncludePreviews ? await WritePreviewsAsync(writer, versionRows, cancellationToken) : 0;

        var cover = await writer.CompleteAsync(request.Name ?? anchor.Name, exportedAt,
            new ArchiveSource(request.ApiRoot, tenantId, tenantName, ServerBuildInfo.Version), recipients, signing, signingKey.History, cancellationToken);
        logger.LogInformation("Wrote an offline archive of {AnchorId}: {Documents} document(s), {Versions} version(s), {Previews} preview(s), "
            + "sealed to {Recipients} certificate(s)", anchor.Id, documentIds.Count, versionRows.Count, previewCount, recipients.Count);
        return new SealedArchiveWritten(cover, documentIds.Count, versionRows.Count, previewCount);
    }

    // The exporter, the chosen members, the external recipients and the recovery certificate, each certificate a
    // recipient of the one key envelope. Every one is resolved BEFORE anything is read, so a refusal costs nothing.
    private async Task<List<SealedArchiveRecipient>> RecipientsAsync(SealedArchiveRequest request, CancellationToken cancellationToken)
    {
        var recoveryPem = await recovery.CurrentPemAsync(cancellationToken) ?? throw new RecoveryCertificateRequiredException();
        var exporterId = currentUser.UserId ?? throw new ArchiveRecipientWithoutCertificateException("A service account");

        var recipients = new List<SealedArchiveRecipient>();
        foreach (var (userId, kind) in request.MemberUserIds.Where(id => id != exporterId).Distinct()
                     .Select(id => (id, ArchiveRecipientKind.Member)).Prepend((exporterId, ArchiveRecipientKind.Exporter)))
        {
            var name = await dbContext.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ArchiveRecipientWithoutCertificateException($"The member {userId}");
            var (certificates, _) = await readerCertificates.ForUserAsync(userId, cancellationToken);
            if (certificates.Count == 0)
            {
                throw new ArchiveRecipientWithoutCertificateException(name);
            }

            recipients.AddRange(certificates.Select(pem => new SealedArchiveRecipient(name, kind, X509Certificate2.CreateFromPem(pem))));
        }

        foreach (var external in request.ExternalRecipients)
        {
            RecipientCertificate.Validate(external.CertificatePem);   // 400 INVALID_RECIPIENT_CERTIFICATE, by the external-link rule
            recipients.Add(new SealedArchiveRecipient(external.Label, ArchiveRecipientKind.External, X509Certificate2.CreateFromPem(external.CertificatePem)));
        }

        recipients.Add(new SealedArchiveRecipient("Recovery certificate", ArchiveRecipientKind.Recovery, X509Certificate2.CreateFromPem(recoveryPem)));
        return recipients;
    }

    // Sync preparation (ADR 0961; write-back later, ADR 0794): where it came from, each document's tag as of the export
    // (the If-Match a later write sends) with the version it was current at and every version's hash, and an empty
    // change journal the reader appends to.
    private async Task WriteSyncAsync(SealedArchiveWriter writer, string apiRoot, Guid tenantId, DateTimeOffset exportedAt,
        IReadOnlyCollection<Guid> documentIds, IReadOnlyList<JsonElement> versionRows, CancellationToken cancellationToken)
    {
        await writer.AddAsync("sync/source.json", JsonSerializer.SerializeToUtf8Bytes(new { apiRoot, tenantId, exportedAt }, Json), cancellationToken);

        var tags = await dbContext.Documents.Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.ConcurrencyToken, d.CurrentVersionId })
            .ToListAsync(cancellationToken);
        var versionsByDocument = versionRows.ToLookup(v => v.GetProperty("documentId").GetGuid());
        var lines = new StringBuilder();
        foreach (var tag in tags.OrderBy(t => t.Id))
        {
            lines.Append(JsonSerializer.Serialize(new
            {
                documentId = tag.Id,
                etag = $"\"{tag.ConcurrencyToken}\"",
                currentVersionId = tag.CurrentVersionId,
                versions = versionsByDocument[tag.Id].Select(v => new
                {
                    id = v.GetProperty("id").GetGuid(),
                    versionNumber = v.GetProperty("versionNumber").GetInt32(),
                    sha256 = v.GetProperty("sha256").GetString(),
                }),
            }, Json)).Append('\n');
        }

        await writer.AddAsync("sync/tags.jsonl", Encoding.UTF8.GetBytes(lines.ToString()), cancellationToken);
        await writer.AddAsync("sync/journal.jsonl", Array.Empty<byte>(), cancellationToken);
    }

    // The previews, opted in: each version's displayed object(s) where it differs from the content (a rendition),
    // rendered now where missing; stored content-addressed beside the content, listed in tree/previews.jsonl. A preview
    // that cannot be produced is left out and said (Warning): the version's content still travels.
    private async Task<int> WritePreviewsAsync(SealedArchiveWriter writer, IReadOnlyList<JsonElement> versionRows, CancellationToken cancellationToken)
    {
        var ids = versionRows.Select(v => v.GetProperty("id").GetGuid()).ToList();
        var keys = await dbContext.DocumentVersions.Where(v => ids.Contains(v.Id)).Select(v => new { v.Id, v.ObjectKey }).ToListAsync(cancellationToken);
        var lines = new StringBuilder();
        var count = 0;
        foreach (var version in keys.OrderBy(k => ids.IndexOf(k.Id)))
        {
            IReadOnlyList<string> displayed;
            try
            {
                displayed = await previews.GetDisplayObjectKeysAsync(version.ObjectKey, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "No preview of version {VersionId} could be rendered for an offline archive; it travels without one", version.Id);
                continue;
            }

            if (displayed.Count == 1 && displayed[0] == version.ObjectKey)
            {
                continue;   // displayed as it is: the content is the preview
            }

            for (var page = 0; page < displayed.Count; page++)
            {
                await using var stream = await storage.GetObjectForClientAsync(displayed[page], "an offline archive", cancellationToken);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
                if (!writer.Paths.Contains($"blobs/{sha}"))
                {
                    await writer.AddAsync($"blobs/{sha}", buffer.ToArray(), cancellationToken);
                }

                lines.Append(JsonSerializer.Serialize(new { versionId = version.Id, page, blobRef = sha, fileExtension = Path.GetExtension(displayed[page]) }, Json)).Append('\n');
                count++;
            }
        }

        await writer.AddAsync("tree/previews.jsonl", Encoding.UTF8.GetBytes(lines.ToString()), cancellationToken);
        return count;
    }

    private static IEnumerable<JsonElement> Rows(IReadOnlyDictionary<string, byte[]> files, string path) =>
        files.TryGetValue(path, out var bytes)
            ? Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            : [];
}
