using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Workflow;
using SimplArchive.Api.Controllers;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

public sealed record VersionRow(
    Guid Id, Guid DocumentId, DocumentVersionStatus Status, int? VersionNumber, string ObjectKey,
    string? Sha256Hash, DateTimeOffset CreatedAt, DateOnly DocumentDate, TimeOnly? DocumentTime,
    Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, string? OcrLanguages, string? Comment,
    OcrVerdict? OcrVerdict = null, bool? IsSigned = null);

/// <summary>
/// Builds a <see cref="DocumentVersionResource"/> — its links, its preview address and its creator's name.
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of <c>DocumentVersionsController</c> (#1220). It is not the HTTP edge: it binds nothing,
/// authorizes nothing and decides no status code. It shapes a response, which is the growth CLAUDE.md says
/// belongs in a named class in the Api project rather than in the controller — and it was 128 lines of a file
/// that had reached 991 before this change and could not take another line.
/// </para>
/// <para>
/// It takes the three collaborators it genuinely used and nothing else, which is how we know the lift was
/// clean: no <c>Url</c>, no <c>Request</c>, no caller identity. A builder that needed those would have been
/// the HTTP edge wearing a different name.
/// </para>
/// </remarks>
public sealed class DocumentVersionResourceBuilder(
    SimplArchiveDbContext dbContext,
    IDocumentPreviewService previews,
    IObjectStorageClient storage,
    StrictEnvelopeDelivery envelopes,
    SimplArchive.Infrastructure.Storage.OmittableLinks omittable,
    Audience.ContentAudience audience)
{
    // The same window the upload URL uses. A presigned link is handed to a browser that is about to follow it,
    // so it wants to be short; fifteen minutes survives a slow render without leaving a usable URL in a log.
    private static readonly TimeSpan PresignedUrlExpiry = TimeSpan.FromMinutes(15);

    // What a sealed document's content is SERVED as (ADR 0975). The clients' one content funnel decides whether
    // to open a response by the type it was served, so the envelope has to say what it is — the stored object
    // carries whatever the uploader sent, which is a guess and never the contract.
    private const string SealedContentType = "application/pkcs7-mime";

    // Whether a document's content arrives as an envelope, answered once per request: a version LIST asks this
    // for every row and the answer belongs to the DOCUMENT (the mask is assigned on the document, not per
    // version). Cached per builder, which is scoped — one request.
    private readonly Dictionary<Guid, bool> _servesContentAsEnvelope = [];

    /// <summary>
    /// Whether this document's content IS a CMS envelope — a Sealed document (ADR 0975) — so the ordinary content
    /// funnel on the device is what opens it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the Sealed Document, not every sealed mask.</b> A Password Entry is also sealed
    /// (<see cref="WellKnownMaskIds.SealedContentMasks"/>), but its STORE client opens the envelope itself from
    /// opaque bytes (ADR 0964) — typing those bytes as an envelope would make the funnel open them first and the
    /// store open them again. Two masks, two doorways, on purpose.
    /// </para>
    /// <para>
    /// Read through the SOFT-DELETED filter too: the recycle bin previews a deleted document's versions (ADR
    /// "Recycle bin tab"), and the mask decides how its content is served there as well.
    /// </para>
    /// </remarks>
    public async Task<bool> ServesContentAsEnvelopeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_servesContentAsEnvelope.TryGetValue(documentId, out var known))
        {
            return known;
        }

        var maskId = await dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"])
            .Where(d => d.Id == documentId)
            .Select(d => dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => v.MaskId).FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);

        return _servesContentAsEnvelope[documentId] = maskId == WellKnownMaskIds.SealedDocument;
    }

    public async Task<DocumentVersionResource> BuildAsync(VersionRow version, string documentName, CancellationToken cancellationToken)
    {
        var links = new List<Link> { new("self", $"/api/documents/{version.DocumentId}/versions/{version.Id}", "GET") };

        // Declared here rather than inside the confirmed-version branch below, because the resource is built for
        // a pending version too and the answer — does this TENANT envelope? — is the same either way.
        var enveloping = false;
        var previewConverted = false;

        // A SEALED document's content is the envelope itself, so there is nothing for the server to envelope:
        // it holds no key for it (ADR 0975). The stored bytes are served as the S/MIME message they are, and
        // both clients' one content funnel opens it on the device.
        var sealedContent = await ServesContentAsEnvelopeAsync(version.DocumentId, cancellationToken);

        // CIPHERTEXT ONLY TO THE AUDIENCE (ADR 0983), for EVERY version of a Sealed document or a Password Entry: the
        // old envelopes stay as they are (history, retention, WORM), but somebody removed from the audience can no
        // longer fetch even what was sealed to them. The version itself still lists — the ACL decides who sees the
        // history — without a content rel to follow.
        var contentWithheld = await audience.WithholdsFromCallerAsync(version.DocumentId, cancellationToken);

        if (version.Status == DocumentVersionStatus.Confirmed)
        {
            // Name the download after Document.Name but with the *version object's* extension, so a version
            // whose type differs from the document name saves correctly — e.g. the searchable-PDF successor of
            // a `.tif` document downloads as `<name>.pdf` (ADR "Searchable PDF successor for TIFFs"), and an
            // email named after its subject (no extension) saves as `<subject>.eml` (ADR "Email
            // auto-classification"). Falls back to the raw name when the object key carries no extension.
            var objectExtension = Path.GetExtension(version.ObjectKey);
            var downloadFileName = string.IsNullOrEmpty(objectExtension)
                ? documentName
                : Path.GetFileNameWithoutExtension(documentName) + objectExtension;

            // THE STRICT TIER'S DELIVERY (#1393, ADR 0828). The seam refuses to presign, correctly — no URL
            // can be an envelope — so these two rels point at a core-served route that envelopes the bytes to
            // the reader instead. The REL NAMES are unchanged, which is the point of ADR 0543's "rel names,
            // not paths, are the compatibility surface": a client follows `download` and gets what this tenant
            // can give it.
            //
            // And they are emitted ONLY when this reader has a usable certificate. A rel that is always
            // present and sometimes fails is the lying affordance 0543 exists to prevent; absent, it means
            // exactly "not available to you, here, now", and the client offers enrolment instead of a button.
            // BOTH QUESTIONS, ASKED SEPARATELY (#1512, ADR 0865). Delivery says what an ADDITIONAL door
            // offers; only the DOOR posture closes one. Reading `enveloping` as "and therefore no plaintext"
            // made SealedDeliveryPermissive indistinguishable from Strict for a browser — which cannot follow
            // the enveloped route at all (it authorizes by header, and the content funnel sends no bearer), so
            // the measured symptom was five 401s and no preview, on the one tier whose definition is that its
            // ordinary doors keep serving.
            enveloping = !sealedContent && await envelopes.AppliesAsync(cancellationToken);
            var doors = ContentDoors.For(
                deliversEnvelopes: enveloping,
                hasReaderCertificate: enveloping
                    && (await envelopes.ReaderCertificatePemsAsync(cancellationToken)).Count > 0,
                refusesPlaintextDoors: await envelopes.RefusesPlaintextDoorsAsync(cancellationToken),
                servesSealedContent: sealedContent);

            if (doors.Enveloped && !contentWithheld)
            {
                var enveloped = $"/api/documents/{version.DocumentId}/versions/{version.Id}/enveloped-content";
                links.Add(new Link("download", enveloped, "GET"));
                links.Add(new Link("preview", $"{enveloped}?inline=true", "GET"));

                // AND SAY WHETHER THAT PREVIEW IS A CONVERSION (#1454). This branch used to leave
                // previewConverted at its `false` default, because the only place it was ever assigned is the
                // non-enveloped branch below — so on a strict tenant the "converted preview" badge could never
                // appear for anybody, whatever the format.
                //
                // It is not that strict tenants serve originals: an inline enveloped read resolves the DISPLAY
                // object (ADR 0828) exactly as the plaintext path does, so an office document or a TIFF is
                // rendered server-side and the envelope carries the RENDITION. The reader was simply never told,
                // and the badge's absence reads as "this is the original" — the one thing it exists to deny.
                //
                // Asked the same way the delivery asks it — GetDisplayObjectKeyAsync, the call
                // DocumentVersionEnvelopedContentController makes for `inline` — so the badge and the bytes
                // cannot disagree. A second way of deciding "is this converted?" would eventually say something
                // the delivery does not.
                var displayKey = await previews.GetDisplayObjectKeyAsync(version.ObjectKey, cancellationToken);
                previewConverted = !string.Equals(displayKey, version.ObjectKey, StringComparison.Ordinal);
            }
            // THE PLAINTEXT DOOR, wherever this tenant's doors serve — which is every tier but the two that
            // refuse, and on a permissive tenant that means BESIDE the enveloped pair above rather than
            // instead of it (`plain-download`, ADR 0865). A client that cannot open a CMS envelope follows
            // this one; the desktop keeps following `download`.
            //
            // GATED ON THE DOOR QUESTION, not on `!doors.Enveloped`, and that is not merely tidier: the
            // presign refusal lives in EncryptingObjectStorageClient, which is registered ONLY when
            // `Encryption:ServiceUrl` is set — while SealedDeliveryStrict needs no service at all. So a
            // strict-delivery tenant configured without one was handed a real presigned PLAINTEXT rel, with
            // this layer's predicate as the only guard and nothing asking it.
            //
            // A null URL still means the seam refused (#1376), so the rel is OMITTED rather than the resource
            // failing — ADR 0543: a missing rel means "not available to you, here, now", and the client
            // disables the affordance instead of trying. The document's metadata still renders, which is the
            // whole reason the seam answers null rather than throwing.
            if (doors.Plaintext && !contentWithheld
                // Through OmittableLinks: without an encryption service an object encrypted at rest refuses its
                // presign, and this resource drops the link rather than failing (ADR 0953).
                && await omittable.OrOmittedAsync(
                    storage.GetPresignedDownloadUrlAsync(version.ObjectKey, PresignedUrlExpiry, downloadFileName,
                        contentType: sealedContent ? SealedContentType : null, cancellationToken),
                    doors.DownloadRel) is { } downloadUrl)
            {
                links.Add(new Link(doors.DownloadRel, downloadUrl.ToString(), "GET"));
            }

            // Inline-disposition URL the workbench preview renders in place — see ADR "Repositories
            // workbench UI". For formats the browser can't display (TIFF, office docs), this resolves to a
            // cached rendition instead of the original (ADR "Server-side preview renditions", "Office
            // document preview via Gotenberg"). Null when no viewable preview can be produced (conversion
            // failed / converter down) — omit the link so the client shows "No preview available" rather
            // than a blank pane (ADR "Preview fallback when a rendition can't be produced").
            // The preview rel was already added above for an enveloped reader — pointing at the same route with
            // an inline disposition, because what differs between "open it" and "save it" is the disposition
            // and not the bytes. On a permissive tenant BOTH are advertised, so this one becomes
            // `plain-preview` and the two resolutions are both paid; they ask the same display-object
            // question, so the badge cannot disagree between them.
            // A sealed document's preview is the ENVELOPE ITSELF, not a rendition: the content is never rendered
            // (a sealed segment is unrendered by declaration, ADR 0937/0964), and what a reader sees is produced on
            // the device by opening it. So it is presigned directly rather than through the preview service, which
            // would answer null by design — and the S/MIME type rides on the URL for the same reason the download's
            // does (ADR 0975).
            if (doors.Plaintext && !contentWithheld)
            {
                // The TASK, not an awaited result: OrOmittedLinks catches the refusal a presign throws when the
                // object is encrypted at rest and no service can unwrap it (ADR 0953). Awaiting it here would
                // throw before the guard sees it and take the whole version resource down with a 503 — which is
                // exactly what the refused-presign test caught on the sealed path.
                var preview = sealedContent
                    ? SealedPreviewAsync(version, downloadFileName, cancellationToken)
                    : previews.GetPreviewUrlAsync(version.ObjectKey, PresignedUrlExpiry, downloadFileName, cancellationToken);

                if (await omittable.OrOmittedAsync(preview, doors.PreviewRel) is { } shown)
                {
                    links.Add(new Link(doors.PreviewRel, shown.Url.ToString(), "GET"));
                    if (!doors.Enveloped)
                    {
                        previewConverted = shown.IsConverted;
                    }
                }
            }

            // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"). A static link — the
            // endpoint computes/caches the layout on demand and returns 204 for formats with no overlay — so
            // building the resource stays cheap (no OCR/PDF parse here).
            //
            // WITHHELD where that endpoint REFUSES (#1402, ADR 0857). The words are the document (ADR 0829),
            // so the overlay route reconstructs it in full and is refused on a tenant whose doors refuse
            // plaintext — while this rel was advertised on every version regardless. A rel that is always
            // present and sometimes refuses is the lying affordance ADR 0543 exists to prevent, and this one
            // lied in the worst available way: 204 means "nothing to highlight", so a client that followed it
            // and met the refusal had no way to tell "this document has no text" from "this tenant will not
            // tell you". Absent, it means "not available to you, here, now", and the desktop computes the
            // layout from the bytes it already decrypted (ADR 0830).
            //
            // The DOOR question, not the delivery one, and the same predicate the endpoint refuses on —
            // `RefuseIfStrictAsync` asks `RefusesPlaintextDoorsAsync`. Gating on the ENVELOPE instead would
            // withhold it on a SealedDeliveryPermissive tenant whose other doors serve, and keep it on a
            // reader with no certificate whose request still refuses: advertising and accepting have to be
            // the same list, or fixing one half is worse than fixing neither.
            //
            // Read from `doors` rather than asked again (ADR 0865): this overlay IS a plaintext door, so it
            // is the same predicate as the pair above, and one answer serving both is what stops the two
            // drifting into disagreement about the same tenant.
            // A sealed document's words are inside the envelope, so there is no layout for the server to give
            // (ADR 0975): the rel is withheld rather than advertised and then answering 204 — which would read as
            // "this document has no text". The desktop computes the layout from the bytes it decrypted.
            if (doors.Plaintext && !sealedContent)
            {
                links.Add(new Link("text-layout", $"/api/documents/{version.DocumentId}/versions/{version.Id}/text-layout", "GET"));
            }

            // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages"). Static
            // link; the endpoint returns 204 for every other format (the client then uses the single `preview`).
            if (!contentWithheld)
            {
                links.Add(new Link("preview-pages", $"/api/documents/{version.DocumentId}/versions/{version.Id}/preview-pages", "GET"));
            }

            // The version's approval workflow (ADR "Workflow / document state model", 0009). Static link — the
            // endpoint resolves the current status + valid-transition links on demand. The STATUS itself rides
            // in the payload (one indexed lookup, beside the preview resolution this method already pays) so a
            // client can label its workflow affordance without a request per rel (ADR 0557).
            links.Add(new Link("workflow", $"/api/documents/{version.DocumentId}/versions/{version.Id}/workflow", "GET"));

            // Roll back to this version (ADR "Version restore") — copies its content into a new current version.
            // Static link (the action enforces CanEditContent + the frozen/checked-out/workflow guards); the
            // client offers it for older versions.
            links.Add(new Link("restore", $"/api/documents/{version.DocumentId}/versions/{version.Id}/restore", "POST"));

            // Sticky notes / positional annotations pinned to this version's pages (ADR "Document annotations
            // (sticky notes)"). Static link — the endpoint lists them on demand.
            links.Add(new Link("annotations", $"/api/documents/{version.DocumentId}/versions/{version.Id}/annotations", "GET"));

            // This version's issuing date (ADR "System-field search"). Static like `restore` above — the PUT
            // enforces CanEditIndexData plus the frozen/checked-out guards, and resolving all of that per row
            // would put a rights + legal-hold + checkout lookup on every version in the list.
            links.Add(new Link("document-date", $"/api/documents/{version.DocumentId}/versions/{version.Id}/document-date", "PUT"));

            // Force a searchable-PDF successor from this version (#999's Make searchable): the user may
            // overrule the detector — a detector-blind scan, or a re-run with changed languages. CONDITIONAL
            // on what the enforcer refuses (ADR 0543): an OCR-candidate extension, and not signed (OCR would
            // break the signature). DocumentSearchableController is the enforcer of the same predicate.
            if (DocumentSearchableController.IsOcrCandidate(version.ObjectKey) && version.IsSigned != true)
            {
                links.Add(new Link("make-searchable", $"/api/documents/{version.DocumentId}/versions/{version.Id}/searchable", "PUT"));
            }
        }

        return new DocumentVersionResource
        {
            Id = version.Id,
            VersionNumber = version.VersionNumber,
            ObjectKey = version.ObjectKey,
            Sha256Hash = version.Sha256Hash,
            Status = version.Status.ToString(),
            WorkflowStatus = (await dbContext.WorkflowStates
                .Where(w => w.DocumentVersionId == version.Id)
                .Select(w => (WorkflowStatus?)w.Status)
                .FirstOrDefaultAsync(cancellationToken))?.ToString(),
            PreviewConverted = previewConverted,
            CreatedAt = version.CreatedAt,
            CreatedByName = await ResolveCreatorNameAsync(version.CreatedByUserId, version.CreatedByServiceAccountId, cancellationToken),
            CreatedByUserId = version.CreatedByUserId,
            DocumentDate = version.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DocumentTime = version.DocumentTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            OcrLanguages = version.OcrLanguages,
            FileExtension = Path.GetExtension(version.ObjectKey),
            Comment = version.Comment,
            OcrVerdict = version.OcrVerdict?.ToString(),
            IsSigned = version.IsSigned == true,
            // Stated rather than left to be inferred from the absent rels (#1352): a strict tenant's version
            // looks exactly like one whose rendition failed, and "no preview available" is the wrong
            // explanation for a browser that cannot open an envelope by construction. A sealed document says
            // so on every tier for the same reason: its one door serves an envelope the browser cannot open.
            ContentIsEnveloped = enveloping || sealedContent,
            Links = links,
        };
    }

    /// <summary>
    /// A sealed document's preview: the stored envelope, presigned directly and typed as what it is (ADR 0975).
    /// Returned as a TASK so the caller's <c>OmittableLinks</c> sees a presign refusal, not an exception from
    /// awaiting it first (ADR 0953).
    /// </summary>
    private async Task<DocumentPreview?> SealedPreviewAsync(VersionRow version, string downloadFileName, CancellationToken cancellationToken)
    {
        var url = await storage.GetPresignedPreviewUrlAsync(
            version.ObjectKey, PresignedUrlExpiry, downloadFileName, SealedContentType, cancellationToken);
        return url is null ? null : new DocumentPreview(url, IsConverted: false);
    }

    private async Task<string> ResolveCreatorNameAsync(Guid? userId, Guid? serviceAccountId, CancellationToken cancellationToken)
    {
        if (userId is { } uid)
        {
            return await dbContext.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "";
        }

        if (serviceAccountId is { } said)
        {
            return await dbContext.ServiceAccounts.Where(s => s.Id == said).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken) ?? "";
        }

        return "";
    }
}
