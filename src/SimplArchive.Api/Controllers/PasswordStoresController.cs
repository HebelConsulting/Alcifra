using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.PasswordStores;
using SimplArchive.Api.PasswordStores;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's password stores (ADR 0964): the list, My Passwords on first use, a store's recipients and the entries it
/// needs sealed again. Reached from the root's <c>passwordStores</c> rel.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/password-stores")]
[Authorize]
public class PasswordStoresController(
    SimplArchiveDbContext dbContext,
    DocumentAccessService access,
    PasswordStoreTree tree,
    PasswordStoreReaders readers,
    PasswordStoreSealing sealing,
    PasswordStoreResourceBuilder resources,
    MyPasswordsProvisioner myPasswords,
    SimplArchive.Api.Audience.ContentAudience audience,
    ICurrentUserAccessor currentUser,
    ICurrentTenantAccessor currentTenant) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var ids = await tree.StoreIdsAsync(cancellationToken);
        var rights = await access.GetCallerRightsForManyAsync(ids, cancellationToken);
        // A PERSON is listed only the stores they can DECRYPT (ADR 0983): the Passwords tab, its tile and apass list a
        // store only to a member of its effective audience. A manager outside it, or an administrator appointing one,
        // reaches the store from the Repositories tab, where it is marked "not encrypted for you". A service account is
        // never a member — it holds no key — and lists every store it may see, because what it does there is seal TO the
        // audience (alcifra's seeding, ADR 0966), never read.
        var visible = new List<Guid>();
        foreach (var id in ids.Where(id => rights.TryGetValue(id, out var r) && r.CanSee))
        {
            if (currentUser.UserId is null
                || (await audience.RootOfAsync(id, cancellationToken) is { } root
                    && await audience.IsMemberAsync(root, currentUser.UserId, cancellationToken)))
            {
                visible.Add(id);
            }
        }

        var rows = await dbContext.Documents
            .Where(d => visible.Contains(d.Id))
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.PersonalRootOwnerId,
                ParentName = dbContext.Documents.Where(p => p.Id == d.ParentId).Select(p => p.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Parent-qualified (ADR 0619), so an administrator who sees another user's My Passwords (the personal-space
        // x-ray, ADR 0670) tells it from their own: "Anna Berger / My Passwords". The caller's own store stays first.
        var me = currentUser.UserId;
        var resource = new PasswordStoreListResource
        {
            Stores =
            [
                .. rows
                    .OrderBy(r => r.PersonalRootOwnerId == me && me is not null ? 0 : 1)
                    .ThenBy(r => ParentQualifiedName.For(r.ParentName, r.Name), StringComparer.CurrentCultureIgnoreCase)
                    .Select(r => new PasswordStoreSummary
                    {
                        Id = r.Id,
                        Name = r.Name,
                        DisplayName = ParentQualifiedName.For(r.ParentName, r.Name),
                        IsPersonal = me is not null && r.PersonalRootOwnerId == me,
                        Links = [new Link("self", PasswordStoreResourceBuilder.FolderHref(r.Id), "GET")],
                    }),
            ],
            Links = [new Link("self", "/api/password-stores", "GET")],
        };

        // My Passwords is the caller's, so only a user is offered it; following the rel is idempotent.
        if (me is not null)
        {
            resource.Links.Add(new Link("my-passwords", "/api/password-stores/mine", "POST"));
        }

        return Ok(resource);
    }

    [HttpHead]
    public async Task<IActionResult> HeadList(CancellationToken cancellationToken)
    {
        await tree.StoreIdsAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>The caller's My Passwords, created on first use: 201 the first time, 200 after.</summary>
    [HttpPost("mine")]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || currentTenant.TenantId is not { } tenantId)
        {
            throw new PasswordStoreNotAvailableException();
        }

        var (storeId, created) = await myPasswords.EnsureAsync(userId, tenantId, cancellationToken);
        var store = await tree.FindAsync(storeId, cancellationToken) ?? throw new NotAPasswordStoreItemException();
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(store.ConcurrencyToken);
        var resource = await resources.FolderAsync(store, cancellationToken);
        return created ? Created(PasswordStoreResourceBuilder.FolderHref(storeId), resource) : Ok(resource);
    }

    /// <summary>The certificates a store's entries must be sealed to, and the readers nothing can be sealed to yet.</summary>
    [HttpGet("{storeId:guid}/recipients")]
    public async Task<IActionResult> Recipients(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await VisibleStoreAsync(storeId, cancellationToken);
        var all = await readers.ForStoreAsync(store.Id, cancellationToken);
        return Ok(new PasswordStoreRecipientsResource
        {
            StoreId = store.Id,
            Certificates =
            [
                .. all.SelectMany(r => r.CertificatePems.Select(pem => new PasswordStoreRecipient
                {
                    UserId = r.UserId, DisplayName = r.DisplayName, CertificatePem = pem,
                })),
            ],
            ReadersWithoutCertificate =
            [
                .. all.Where(r => r.CertificatePems.Count == 0)
                    .Select(r => new PasswordStoreReaderSummary { UserId = r.UserId, DisplayName = r.DisplayName }),
            ],
            Links =
            [
                new Link("self", PasswordStoreResourceBuilder.StoreHref(store.Id, "recipients"), "GET"),
                new Link("store", PasswordStoreResourceBuilder.FolderHref(store.Id), "GET"),
            ],
        });
    }

    [HttpHead("{storeId:guid}/recipients")]
    public async Task<IActionResult> HeadRecipients(Guid storeId, CancellationToken cancellationToken)
    {
        await VisibleStoreAsync(storeId, cancellationToken);
        return NoContent();
    }

    /// <summary>The entries a member with edit rights should seal again (ADR 0964), each with its If-Match.</summary>
    [HttpGet("{storeId:guid}/reseal")]
    public async Task<IActionResult> Reseal(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await VisibleStoreAsync(storeId, cancellationToken);
        if (!(await access.GetCallerRightsAsync(store.Id, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        var resource = new PasswordStoreResealResource
        {
            StoreId = store.Id,
            Links =
            [
                new Link("self", PasswordStoreResourceBuilder.StoreHref(store.Id, "reseal"), "GET"),
                new Link("store", PasswordStoreResourceBuilder.FolderHref(store.Id), "GET"),
                new Link("password-store-recipients", PasswordStoreResourceBuilder.StoreHref(store.Id, "recipients"), "GET"),
            ],
        };
        foreach (var entry in await sealing.EntriesNeedingResealAsync(store, cancellationToken))
        {
            resource.Entries.Add(await resources.EntryAsync(entry, cancellationToken));
        }

        return Ok(resource);
    }

    [HttpHead("{storeId:guid}/reseal")]
    public async Task<IActionResult> HeadReseal(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await VisibleStoreAsync(storeId, cancellationToken);
        return (await access.GetCallerRightsAsync(store.Id, cancellationToken)).CanEditContent ? NoContent() : Forbid();
    }

    private async Task<PasswordItem> VisibleStoreAsync(Guid storeId, CancellationToken cancellationToken) =>
        await tree.FindAsync(storeId, cancellationToken) is { Kind: PasswordItemKind.Store } store
        && (await access.GetCallerRightsAsync(store.Id, cancellationToken)).CanSee
            ? store
            : throw new NotAPasswordStoreItemException();
}
