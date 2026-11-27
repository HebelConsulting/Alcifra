using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors;
using Alcifra.Api.Errors.Exceptions.Ocr;
using Alcifra.Api.Errors.Exceptions.Documents;
using Alcifra.Api.Errors.Exceptions.Concurrency;
using Alcifra.Api.Errors.Exceptions.Checkout;
using Alcifra.Api.Errors.Exceptions.LegalHolds;
using Alcifra.Api.Hypermedia;
using Alcifra.Api.Pagination;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>The create door, split out per the 1000-line rule (#2501's identity choice lives here).</summary>
public partial class DocumentChildrenController
{
    public class CreateChildRequest
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Which kind of folder to create (#564 slice 2, ADR 0620) — omitted means a plain folder, as before.
        /// Only FOLDER masks can be asked for: what an item is gets decided by classifying its content, not
        /// by a caller asserting it.
        /// </summary>
        public string? FolderMask { get; set; }

        /// <summary>
        /// The mask to create, as the <c>admits</c> entry gave it (#678) — the general form of
        /// <see cref="FolderMask"/>, which only names the handful of kinds that ever had a slug.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An id on the wire where this API has otherwise exposed vocabulary, and taken deliberately: the
        /// client is <b>echoing back what the server gave it</b> on the row, exactly as it does with
        /// <c>folderMask</c>, so it composes nothing and keeps no copy of the mask set (ADR 0543). The
        /// alternative was a stable per-mask slug column, which needs uniqueness rules, a migration and an
        /// answer for the existing one-to-many alias — "notes" and "notebook" both mean Notebook, which a slug
        /// column cannot say.
        /// </para>
        /// <para>
        /// Wins over <see cref="FolderMask"/> when both are sent. A client that knows about this field is
        /// newer than one that only knows slugs, and the two can only disagree if something built the body by
        /// hand.
        /// </para>
        /// </remarks>
        public Guid? MaskId { get; set; }

        /// <summary>
        /// The new document's index data, written in the SAME save as the document (#1634), in the shape
        /// <c>PUT …/index-data</c> takes. Omitted means none, as before.
        /// </summary>
        /// <remarks>
        /// One user action, one request (ADR 0794). It is also the only way a mask with REQUIRED fields can be
        /// created inside an exclusive module folder: such a child can only be created WITH its mask, and a create
        /// that carried no values was refused for the very values it had no way to send. Required stays required;
        /// it is checked against what this request carries.
        /// </remarks>
        public List<DocumentMetadataController.SetFieldValueGroup>? Fields { get; set; }

        /// <summary>
        /// The identity to derive the new document's id from (#2501): a stable slug, e.g. <c>tube/channel/alcifra</c>.
        /// Omitted means a fresh GUID, as before.
        /// </summary>
        /// <remarks>
        /// A create carrying a slug is <b>idempotent by identity</b>: the slug derives the id from the tenant
        /// (<see cref="Documents.CallerDocumentIds"/>), an id nobody holds is created and answered <c>201</c>, and an
        /// id that already exists is answered <c>200</c> with that document — never overwritten, never duplicated.
        /// That is what makes a seed re-runnable without a lookup by name, across a reset as across a run. The two
        /// namespaces (caller slugs and the demo seeder's) are frozen constants, so a slug can never resolve to
        /// demo-seeded content.
        /// </remarks>
        public string? IdSlug { get; set; }
    }

    // The folder kinds a caller may name by SLUG. No longer the gate — whether a mask may be created is
    // Mask.UserCreatable and whether it may live here is containment, both data (#678) — this is now a
    // compatibility alias table so a client built before that keeps working, and so the wire stays readable
    // for the kinds that always had a name.
    //
    // It cannot grow to cover a tenant-authored mask, which is exactly why MaskId exists beside it.
    private static readonly Dictionary<string, Guid> CreatableFolderMasks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"] = WellKnownMaskIds.Folder,
        ["calendar"] = WellKnownMaskIds.Calendar,
        ["addressbook"] = WellKnownMaskIds.Addressbook,
        ["notebook"] = WellKnownMaskIds.Notebook,
        // "notes" is the name this kind shipped under and stays accepted: the mask was renamed, and a wire
        // value a client may already be sending is not the place to charge for that.
        ["notes"] = WellKnownMaskIds.Notebook,
        ["section"] = WellKnownMaskIds.NotebookSection,
    };

    // The typed-folder family this mask version belongs to as a FOLDER, or null for an ordinary folder. The
    // rules are data (WellKnownMaskIds.TypedFolderRules), so a new typed family needs no change here.
    private async Task<TypedFolderRule?> TypedFolderRuleOfAsync(Guid maskVersionId, CancellationToken cancellationToken)
    {
        var maskId = await _dbContext.MaskVersions
            .Where(v => v.Id == maskVersionId)
            .Select(v => (Guid?)v.MaskId)
            .SingleOrDefaultAsync(cancellationToken);

        return maskId is { } id ? WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == id) : null;
    }

    [HttpPost("children")]
    public async Task<IActionResult> CreateChild(Guid documentId, [FromBody] CreateChildRequest request, CancellationToken cancellationToken)
    {
        var parent = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.TenantId, d.MaskVersionId, d.PersonalRootOwnerId })
            .SingleOrDefaultAsync(cancellationToken);

        if (parent is null)
        {
            return NotFound();
        }

        // Resolve the requested kind before doing anything else — an unknown one is the caller's mistake,
        // not a half-created folder.
        //
        // MaskId wins over FolderMask: it is the general form, and a client sending it is newer than one
        // sending only a slug. Both come from the `admits` entry the server handed out, so they can disagree
        // only if something assembled the body by hand.
        // A password entry has its store's own door, which carries ADR 0982's rule (#1931): refused here whether named or
        // implied — an unnamed item in a store or password folder is an entry-to-be. Folders there are still made here.
        if (request.MaskId == WellKnownMaskIds.PasswordEntry
            || (request.MaskId is null && request.FolderMask is not { Length: > 0 }
                && parent.MaskVersionId is { } passwordParentVersion
                && await _dbContext.MaskVersions.AnyAsync(v => v.Id == passwordParentVersion
                    && (v.MaskId == WellKnownMaskIds.PasswordStore || v.MaskId == WellKnownMaskIds.PasswordFolder), cancellationToken)))
        {
            throw new Errors.Exceptions.PasswordStores.PasswordEntryUsesItsStoreException();
        }

        // A CI/CD secret likewise (ADR 1084): named, or implied by an unnamed item in a CI/CD store, which holds nothing else.
        if (request.MaskId == WellKnownMaskIds.CiCdSecret
            || (request.MaskId is null && request.FolderMask is not { Length: > 0 }
                && parent.MaskVersionId is { } cicdParentVersion
                && await _dbContext.MaskVersions.AnyAsync(v => v.Id == cicdParentVersion && v.MaskId == WellKnownMaskIds.CiCdStore, cancellationToken)))
        {
            throw new Errors.Exceptions.PasswordStores.CiCdSecretUsesItsStoreException();
        }

        var rules = await _containment.ForAsync(_dbContext, parent.TenantId, cancellationToken);
        var folderMaskId = WellKnownMaskIds.Folder;
        var folderMaskRequested = request.MaskId is not null || request.FolderMask is { Length: > 0 };

        if (request.MaskId is { } requestedMaskId)
        {
            // Gated on the DATA, not on a table of names (#678). A mask this tenant does not have, or one
            // provisioning owns, is refused here — which is what stops a caller asking for a Mailbox or a
            // Repository by id now that ids are on the wire. Containment is asked separately below, so the
            // refusal a caller gets names the right reason.
            //
            // A SEALED mask is the one ITEM mask a caller may name (ADR 0975): its content is an opaque CMS
            // envelope, so classification can never decide what the document is — the caller's assertion is
            // the only source of truth, and without it a Sealed document has no create door at all. Narrower
            // than "every sealed mask": one CONFINED to a typed folder (Password Entry, inside a store or a
            // password folder) keeps its family's own door, which also does that family's work — the mask id
            // aside, filing an entry through here would skip the store's rules.
            var sealedAndUnconfined = WellKnownMaskIds.SealedContentMasks.ContainsKey(requestedMaskId)
                && !WellKnownMaskIds.AdmittingFolders.ContainsKey(requestedMaskId);
            if (!rules.IsUserCreatable(requestedMaskId)
                || !(rules.IsFolderMask(requestedMaskId) || sealedAndUnconfined))
            {
                throw new InvalidFolderMaskException(requestedMaskId.ToString());
            }

            folderMaskId = requestedMaskId;
        }
        else if (request.FolderMask is { Length: > 0 } requestedMask
            && !CreatableFolderMasks.TryGetValue(requestedMask, out folderMaskId))
        {
            throw new InvalidFolderMaskException(requestedMask);
        }

        // Is the PARENT a typed folder (Notebook / Section / Addressbook / Calendar)?
        //
        // Two shapes, and the difference is whether the caller SAID what it wants. A named folderMask is an
        // unambiguous ask: honour it when the parent admits that mask — a Section inside a Notebook — and
        // refuse it with the reason when it does not. An unnamed one is ambiguous, because this endpoint
        // serves both "make a folder" and step one of an upload with the same body, so inside a typed folder
        // it is an item-to-be and must be left MASKLESS for the finalizer, exactly as the CardDAV/IMAP write
        // paths leave theirs.
        //
        // Stamping the Folder mask unconditionally is what made an upload into a typed folder impossible: the
        // containment rule exempts a document whose type is not determined yet (its own comment says so, and
        // names .vcf/.ics), and this endpoint was defeating that exemption before the finalizer ever ran.
        var parentRule = parent.MaskVersionId is { } parentMaskVersionId
            ? await TypedFolderRuleOfAsync(parentMaskVersionId, cancellationToken)
            : null;

        // …and a MODULE's exclusive folder is one too (a Customer admits only an Issued license and a Pull credential,
        // AlcifraLicensing ADR 0003). The core table above knows only the core's own families, while SaveChanges
        // enforces the tenant's whole containment model, module masks included; asking the same rules here is what
        // keeps an unnamed child an item-to-be instead of a Folder that SaveChanges then refuses.
        var parentMaskId = parent.MaskVersionId is { } versionId
            ? await _dbContext.MaskVersions.Where(v => v.Id == versionId).Select(v => (Guid?)v.MaskId).SingleOrDefaultAsync(cancellationToken)
            : null;
        var parentExclusive = parentRule is not null || (parentMaskId is { } exclusiveId && rules.IsExclusiveFolder(exclusiveId));

        var admittedFolder = folderMaskRequested
            && (parentRule?.Admits.Any(a => a.MaskId == folderMaskId) == true
                || (parentMaskId is { } admittingId && rules.AdmittedBy(admittingId).Contains(folderMaskId)));

        if (parentRule is { } rule && folderMaskRequested && !admittedFolder)
        {
            throw new Errors.Exceptions.Documents.TypedFolderContainmentException(
                $"A {rule.FolderName} holds only {rule.AdmittedNames} — '{request.FolderMask}' cannot live there.");
        }

        // A named folder a module's exclusive folder does not admit is refused too, never quietly made an item.
        if (parentExclusive && folderMaskRequested && !admittedFolder)
        {
            throw new Errors.Exceptions.Documents.TypedFolderContainmentException(
                $"'{request.Name}' cannot be created here: this folder holds only the kinds it declares.");
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanCreateSubItems)
        {
            return Forbid();
        }

        var (createdByUserId, createdByServiceAccountId) = _access.GetCallerIdentity();

        // The caller may choose the identity (#2501): the slug derives the id, and a create that names one is
        // idempotent — an id already held answers that document (200), never a duplicate and never an overwrite.
        var id = Guid.NewGuid();
        if (request.IdSlug is { } requestedSlug)
        {
            var slug = requestedSlug.Trim();
            if (slug.Length is 0 or > 200)
            {
                throw new Errors.Exceptions.Documents.InvalidDocumentIdSlugException();
            }

            id = CallerDocumentIds.For(parent.TenantId, slug);
            if (await _dbContext.Documents.Where(d => d.Id == id).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken) is { } existingName)
            {
                return Ok(new DocumentSummaryResource
                {
                    Id = id,
                    Name = existingName,
                    Links =
                    [
                        new Link("self", $"/api/documents/{id}", "GET"),
                        new Link("versions", $"/api/documents/{id}/versions", "GET"),
                    ],
                });
            }
        }

        var child = new Document
        {
            Id = id,
            TenantId = parent.TenantId,
            ParentId = documentId,
            Name = request.Name,
            // Assigned the Folder mask now; if a version is later added, finalize reclassifies it (ADR "Folder mask on folders").
            // A typed folder (#564) instead wears the mask the request named — resolved tenant-explicitly, so a
            // caller with no ambient tenant can't produce a maskless folder (ADR 0590's defect).
            // Inside a typed folder, null UNLESS the caller named a mask that folder admits (a Section in a
            // Notebook): an unnamed one is an item-to-be, and the finalizer decides what it is (see above).
            MaskVersionId = parentExclusive && !admittedFolder
                ? Guid.Empty
                : await Documents.FolderMask.CurrentVersionIdAsync(_dbContext, parent.TenantId, folderMaskId, cancellationToken)
                    ?? await Documents.FolderMask.CurrentVersionIdAsync(_dbContext, cancellationToken)
                    ?? Guid.Empty,
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.Documents.Add(child);
        if (Audience.ContentAudience.DeclaredBy(folderMaskId) is not null)
        {
            // The creator becomes the first audience manager, in the same commit (ADR 0983).
            await _audience.SeedForNewAsync(child.Id, parent.TenantId, documentId, parent.PersonalRootOwnerId, createdByUserId, cancellationToken);
        }

        if (request.Fields is { Count: > 0 } fields)
        {
            await _indexData.ApplyAsync(child, fields, confirmDuplicateClaims: false, cancellationToken);
        }

        // Only the sibling-name rule is a name conflict (#1634). The shared translation turns the field and
        // containment rules into their own errors, and anything else surfaces as itself instead of as a false 409.
        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (Domain.Documents.DocumentNameNotUniqueException)
        {
            throw DocumentNameConflictException.OnSameParent();
        }

        await _queue.EnqueueAsync(child.Id, cancellationToken);
        await _audit.RecordAsync(AuditActions.DocumentCreated, "Document", child.Id, child.Name, cancellationToken: cancellationToken);

        // `versions` alongside `self` because creating a child is step one of a THREE-step upload (create,
        // add a version, finalize), and a create response that hands back only an id is precisely what forces
        // the next two steps to be composed from it (ADR 0543, issue #416).
        var resource = new DocumentSummaryResource
        {
            Id = child.Id,
            Name = child.Name,
            Links =
            [
                new Link("self", $"/api/documents/{child.Id}", "GET"),
                new Link("versions", $"/api/documents/{child.Id}/versions", "GET"),
            ],
        };

        return CreatedAtAction(nameof(DocumentsController.Get), "Documents", new { documentId = child.Id }, resource);
    }
}
