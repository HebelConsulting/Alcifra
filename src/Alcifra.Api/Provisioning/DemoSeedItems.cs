using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Users;
using Alcifra.Domain.Workflow;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// The building blocks every demo seed files its samples with: a folder, a document and its versions, an
/// annotation, a review task, a reference, an external link, a typed item from composed text.
/// </summary>
/// <remarks>
/// <para>
/// One place, because four seeders use them (the Demo and Manual tenants through <see cref="KristallSamples"/>,
/// the Crypto tenant, and the module demos' setup) and a copy per seeder is how the deterministic-id rule
/// (#781) would be kept in three of them and forgotten in the fourth.
/// </para>
/// <para>
/// Every id is derived from the tenant root and a stable slug (<see cref="DemoId"/>), never minted: the kiosk
/// reseeds nightly, and fresh GUIDs each morning would hand every caching client a brand-new server wearing
/// yesterday's names.
/// </para>
/// </remarks>
internal static class DemoSeedItems
{
    internal static async Task<Document> AddFolderAsync(
        AlcifraDbContext dbContext, Guid tenantId, Guid parentId, string name, Guid adminId, DateTimeOffset at, Guid? folderMaskVersionId, string slug)
    {
        var folder = new Document
        {
            // The SLUG, not the display name, is the identity (#781) — renaming a seeded folder must not
            // change what a client cached about it.
            Id = DemoId.For(tenantId, $"folder/{slug}"),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = folderMaskVersionId ?? Guid.Empty,
            CreatedByUserId = adminId,
            CreatedAt = at,
        };
        dbContext.Documents.Add(folder);
        await dbContext.SaveChangesAsync();
        return folder;
    }

    internal static async Task<Document> AddDocumentAsync(
        AlcifraDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Guid tenantId, Guid parentId, string name, Guid adminId, DateTimeOffset at, Guid? maskVersionId,
        string resourceName, string ext, string contentType, DateOnly documentDate,
        DocumentFinalizer finalizer, string? comment = null)
    {
        // The embedded-resource stem is the identity (#781): a code-level literal, unique per seeded document,
        // and stable when the DISPLAY name changes.
        var stem = Path.GetFileNameWithoutExtension(resourceName);
        var storageFolderId = DemoId.For(tenantId, $"doc/{stem}/storage");
        var document = new Document
        {
            Id = DemoId.For(tenantId, $"doc/{stem}"),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = maskVersionId ?? Guid.Empty,
            CreatedByUserId = adminId,
            CreatedAt = at,
            StorageFolderId = storageFolderId,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync();

        await AddVersionAsync(dbContext, storage, assembly, document, adminId, at, storageFolderId, resourceName, ext, contentType, documentDate, finalizer, comment);
        return document;
    }

    internal static async Task<DocumentVersion> AddVersionAsync(
        AlcifraDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Document document, Guid adminId, DateTimeOffset at, Guid storageFolderId,
        string resourceName, string ext, string contentType, DateOnly documentDate,
        DocumentFinalizer finalizer, string? comment = null)
    {
        var bytes = await ReadResourceAsync(assembly, resourceName);
        var versionId = DemoId.For(document.TenantId, $"version/{Path.GetFileNameWithoutExtension(resourceName)}");
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, document.CreatedAt, storageFolderId, versionId, ext);
        using (var content = new MemoryStream(bytes))
        {
            await storage.PutObjectAsync(objectKey, content, contentType);
        }

        // Created PENDING and confirmed through DocumentFinalizer — the same path an interactive upload takes,
        // rather than a parallel one that writes a Confirmed row directly. That is what gives the demo its
        // automatic chat entries (ADR 0545).
        //
        // The finalizer owns what it computes, so the fields below are deliberately NOT set here: it assigns the
        // version number, hashes the blob server-side from object storage rather than trusting a value we hand
        // it, and stamps SizeBytes. It also counts the blob toward the tenant's quota, so the quota UI shows
        // real usage instead of zero.
        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = adminId,
            DocumentDate = documentDate,
            CreatedAt = at,
            // The check-in comment — why this version exists. Deliberately not set on every version: the feed
            // renders "Version N" with the comment beneath it when there is one and just "Version N" when there
            // isn't, and the demo should show both (issue #380).
            Comment = comment,
        };
        // ONE unit of work per seeded document (#1171): a half-filed demo document is still a demo that looks
        // broken, and the finalizer refuses to run outside its own transaction.
        await finalizer.FileAsync(version, CancellationToken.None);
        return version;
    }

    internal static void AddAnnotation(
        AlcifraDbContext dbContext, Guid tenantId, Guid documentId, Guid versionId, Guid adminId, DateTimeOffset at,
        AnnotationKind kind, double x, double y, double width, double height, string text, string color)
    {
        dbContext.DocumentAnnotations.Add(new DocumentAnnotation
        {
            Id = DemoId.For(tenantId, $"annotation/{documentId}/{kind}/{x},{y}"),
            TenantId = tenantId,
            DocumentId = documentId,
            DocumentVersionId = versionId,
            PageIndex = 0,
            Kind = kind,
            PositionX = x,
            PositionY = y,
            Width = width,
            Height = height,
            Text = text,
            Color = color,
            CreatedByUserId = adminId,
            CreatedAt = at,
            UpdatedAt = at,
        });
    }

    internal static async Task AddInReviewWorkflowAsync(
        AlcifraDbContext dbContext, Guid tenantId, Guid versionId, Guid adminId, DateTimeOffset now)
    {
        var workflowState = new WorkflowState
        {
            Id = DemoId.For(tenantId, $"workflow/{versionId}"),
            TenantId = tenantId,
            DocumentVersionId = versionId,
            Status = WorkflowStatus.InReview,
            AssignedToUserId = adminId,
            CreatedAt = now,
            UpdatedAt = now,
            // Hand-set overdue (deadline yesterday) so the Tasks tab shows the overdue badge and the escalation
            // sweep has something to act on (ADR "Workflow escalation / SLA reminders").
            DueAt = now.AddDays(-1),
        };
        dbContext.WorkflowStates.Add(workflowState);
        dbContext.WorkflowTransitions.Add(new WorkflowTransition
        {
            Id = DemoId.For(tenantId, $"workflow/{versionId}/t1"),
            TenantId = tenantId,
            WorkflowStateId = workflowState.Id,
            FromStatus = WorkflowStatus.Draft,
            ToStatus = WorkflowStatus.InReview,
            AssignedToUserId = adminId,
            PerformedByUserId = adminId,
            CreatedAt = now,
        });
        await dbContext.SaveChangesAsync();
    }

    internal static async Task AddReferenceAsync(
        AlcifraDbContext dbContext, Guid tenantId, Guid parentFolderId, Guid targetDocumentId, Guid adminId, DateTimeOffset at)
    {
        dbContext.DocumentReferences.Add(new DocumentReference
        {
            Id = DemoId.For(tenantId, $"ref/{parentFolderId}/{targetDocumentId}"),
            TenantId = tenantId,
            ParentFolderId = parentFolderId,
            TargetDocumentId = targetDocumentId,
            CreatedByUserId = adminId,
            CreatedAt = at,
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>A live external link a visitor can click rather than read about (ADR 0546, issue #405).</summary>
    /// <remarks>
    /// <para>
    /// <b>Static token.</b> The kiosk re-seeds nightly from empty volumes, so a fresh random token would mint a
    /// new URL every night and break every link already shared, bookmarked or written into a demo script. Derived
    /// from a fixed string instead — see <c>ExternalLinkToken.DeriveForDemoSeed</c> for why that is safe here and
    /// nowhere else. The token is unique across the installation, so each tenant passes its own seed string.
    /// </para>
    /// <para>
    /// <b>90 days</b>, so the nightly reset always refreshes it well before it lapses, and <b>unlimited
    /// accesses</b>: the tenant default of 5 would be exhausted by five curious visitors.
    /// </para>
    /// </remarks>
    internal static async Task SeedExternalLinkAsync(
        AlcifraDbContext dbContext, Guid tenantId, Guid documentId, Guid adminId, DateTimeOffset now,
        string slug, string tokenSeed)
    {
        dbContext.ExternalLinks.Add(new ExternalLink
        {
            Id = DemoId.For(tenantId, slug),
            TenantId = tenantId,
            DocumentId = documentId,
            Token = ExternalLinkToken.DeriveForDemoSeed(tokenSeed),
            ExpiresAt = now.AddDays(90),
            // null = unlimited, NOT the tenant's ExternalLinkDefaultAccesses.
            MaxAccesses = null,
            CreatedByUserId = adminId,
            CreatedAt = now,
        });

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Gives the demo logins the SAME known password for WebDAV/CalDAV/CardDAV and IMAP that they use to log in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are APP-SPECIFIC credentials that normally do not exist until a user generates one — deliberately,
    /// so the real login password is never typed into an OS keychain. That is right for a product and wrong for
    /// a demo: nobody could mount the drive or add the mailbox without first finding two dialogs.
    /// </para>
    /// <para>
    /// Set only when ABSENT: a visitor who rotated their own credential keeps it (#574's lesson). Demo-only by
    /// construction: only the env-gated demo seeders reach it, so a production deployment never does.
    /// </para>
    /// </remarks>
    internal static async Task EnableDavAndImapAsync(
        AlcifraDbContext dbContext, PasswordHasher<User> hasher, string demoPassword, Guid[] userIds, DateTimeOffset now)
    {
        // As the earlier passwords' devices (#1970), so a visitor's mail client finds mail, files, calendars and contacts
        // with the one demo password, as before.
        foreach (var user in await dbContext.Users.Where(u => userIds.Contains(u.Id)).ToListAsync())
        {
            foreach (var kind in new[] { ProtocolDeviceKind.LegacyDav, ProtocolDeviceKind.LegacyImap })
            {
                if (!await Alcifra.Api.ProtocolDevices.LegacyProtocolDevices.ExistsAsync(dbContext, user.Id, kind, CancellationToken.None))
                {
                    dbContext.ProtocolDevices.Add(
                        Alcifra.Api.ProtocolDevices.LegacyProtocolDevices.New(user, kind, hasher.HashPassword(user, demoPassword), now));
                }
            }
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Paints the calendar with its own colour — the collection's default, which every user sees until they set
    /// a personal override (ADR 0620).
    /// </summary>
    internal static async Task SetColourAsync(
        AlcifraDbContext dbContext, Guid tenantId, Document folder, Guid? maskVersionId, string colour)
    {
        if (maskVersionId is not { } maskVersion)
        {
            return;
        }

        var fieldId = await dbContext.FieldDefinitions
            .Where(f => f.Name == "Colour" && f.MaskVersionId == maskVersion)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync();

        if (fieldId is not { } field
            || await dbContext.FieldValues.AnyAsync(v => v.DocumentId == folder.Id && v.FieldDefinitionId == field))
        {
            return;
        }

        dbContext.FieldValues.Add(new FieldValue
        {
            Id = DemoId.For(tenantId, $"field-value/{folder.Id}/{field}"),
            TenantId = tenantId,
            DocumentId = folder.Id,
            FieldDefinitionId = field,
            Value = colour,
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>A department mailbox's address claim (#703), written only when absent (#574's rule).</summary>
    internal static async Task ClaimAsync(AlcifraDbContext dbContext, Guid tenantId, Guid mailboxId, string address)
    {
        var fieldId = await dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Mailbox && v.IsCurrent)
            .Join(dbContext.FieldDefinitions, v => v.Id, f => f.MaskVersionId, (_, f) => f)
            .Where(f => f.Name == Infrastructure.Masks.WellKnownMaskSeeder.MailboxAddressesFieldName)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync();
        if (fieldId is not { } field || await dbContext.FieldValues.AnyAsync(v => v.DocumentId == mailboxId && v.FieldDefinitionId == field))
        {
            return;
        }

        dbContext.FieldValues.Add(new FieldValue
        {
            Id = DemoId.For(tenantId, $"field-value/{mailboxId}/{field}"),
            TenantId = tenantId,
            DocumentId = mailboxId,
            FieldDefinitionId = field,
            Value = address,
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// One typed item from composed text (an appointment's iCalendar, a contact's vCard). MASKLESS on purpose:
    /// the finalizer classifies a <c>.vcf</c> as a Contact and an <c>.ics</c> as an Appointment once the bytes
    /// are there, and it is also what extracts the UID the DAV correlation depends on. Stamping a mask here would
    /// guess at what classification decides.
    /// </summary>
    /// <remarks>
    /// Get-or-create, so a restart against an existing volume neither duplicates nor throws. The slug keeps the
    /// <c>doc/artists/</c> family it was born in (the retired events example, #1788): renaming it would move
    /// every seeded appointment to a new id on the next reseed, which is exactly the churn #781 removed.
    /// </remarks>
    internal static async Task<Document?> ItemAsync(
        AlcifraDbContext dbContext, IObjectStorageClient storage, Guid tenantId, Guid parentId, string name,
        Guid adminId, DateTimeOffset at, DocumentFinalizer finalizer, string text, string extension, string contentType)
    {
        if (await dbContext.Documents.FirstOrDefaultAsync(d => d.ParentId == parentId && d.Name == name) is { } existing)
        {
            return existing;
        }

        var storageFolderId = DemoId.For(tenantId, $"doc/artists/{parentId}/{name}/storage");
        var versionId = DemoId.For(tenantId, $"doc/artists/{parentId}/{name}/v1");
        var document = new Document
        {
            Id = DemoId.For(tenantId, $"doc/artists/{parentId}/{name}"),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            CreatedByUserId = adminId,
            CreatedAt = at,
            StorageFolderId = storageFolderId,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync();

        var objectKey = ObjectKeyBuilder.Build(tenantId, at, storageFolderId, versionId, extension);
        using (var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)))
        {
            await storage.PutObjectAsync(objectKey, content, contentType);
        }

        // Pending + the shared finalizer, never a hand-written Confirmed version: the status is guarded by a
        // CHECK constraint, and the finalizer is what classifies and indexes it.
        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = tenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = adminId,
            CreatedAt = at,
            DocumentDate = DateOnly.FromDateTime(at.UtcDateTime),
        };
        await finalizer.FileAsync(version, CancellationToken.None);
        return document;
    }

    internal static async Task<byte[]> ReadResourceAsync(Assembly assembly, string logicalName)
    {
        await using var resource = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource {logicalName} was not found.");
        using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
