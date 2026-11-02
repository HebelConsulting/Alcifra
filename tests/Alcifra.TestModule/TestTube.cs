using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.ModuleAbi;

namespace Alcifra.TestModule;

/// <summary>The anonymous prefix's fixture (ABI 1.28): who the core resolved the request for, and a player's link to a
/// document, read as the module's principal with nobody signed in.</summary>
[ApiController]
[AllowAnonymous]
[Route("peertube/test-tube")]
public sealed class TestTubeController(IModuleCallerContext caller, IModuleArchiveFacade archive) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Ok(new
    {
        tenantId = caller.TenantId,
        userId = caller.UserId,
        serviceAccountId = caller.ServiceAccountId,
        encryptsAtRest = await caller.TenantEncryptsAtRestAsync(cancellationToken),
    });

    [HttpHead]
    public IActionResult Head() => Ok();

    [HttpGet("{documentId:guid}/link")]
    public async Task<IActionResult> Link(Guid documentId, CancellationToken cancellationToken) =>
        await archive.GetDocumentContentUrlAsync(documentId, TimeSpan.FromMinutes(1), "video/mp4", inline: true, cancellationToken) is { } url
            ? Ok(new { url = url.ToString() })
            : NotFound();

    [HttpHead("{documentId:guid}/link")]
    public Task<IActionResult> LinkHead(Guid documentId, CancellationToken cancellationToken) => Link(documentId, cancellationToken);
}
