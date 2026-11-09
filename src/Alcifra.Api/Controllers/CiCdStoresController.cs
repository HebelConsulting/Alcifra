using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.PasswordStores;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The caller's CI/CD stores (ADR 1084), reached from the root's <c>cicdStores</c> rel. Each one is a sealed store like a
/// Password Store: its rows, entries, recipients and re-seal follow the same rels from its own resource.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/cicd-stores")]
[Authorize]
public class CiCdStoresController(SealedStoreListing listing, PasswordStoreTree tree) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await listing.ListAsync(SealedStoreFamily.CiCd, "/api/cicd-stores", cancellationToken));

    [HttpHead]
    public async Task<IActionResult> HeadList(CancellationToken cancellationToken)
    {
        await tree.StoreIdsAsync(SealedStoreFamily.CiCd, cancellationToken);
        return NoContent();
    }
}
