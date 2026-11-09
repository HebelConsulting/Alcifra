using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.PasswordStores;

/// <summary>
/// The stores of one sealed-store family the caller may use (ADR 0964, ADR 1084): one listing for the Password Stores and
/// the CI/CD Stores, so who sees which store is decided once.
/// </summary>
public sealed class SealedStoreListing(
    AlcifraDbContext dbContext,
    DocumentAccessService access,
    PasswordStoreTree tree,
    Alcifra.Api.Audience.ContentAudience audience,
    ICurrentUserAccessor currentUser)
{
    /// <summary>The family's stores, the caller's personal one first, then by their parent-qualified name.</summary>
    public async Task<PasswordStoreListResource> ListAsync(SealedStoreFamily family, string selfHref, CancellationToken cancellationToken)
    {
        var ids = await tree.StoreIdsAsync(family, cancellationToken);
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
        return new PasswordStoreListResource
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
            Links = [new Link("self", selfHref, "GET")],
        };
    }
}
