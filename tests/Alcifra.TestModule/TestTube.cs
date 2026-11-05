using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.ModuleAbi;

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

/// <summary>The Tube tab's stand-in (#2148): a canned answer in the module's shape, since this repository can't load the module.</summary>
[ApiController]
[Route("api/test-module/tube-channels")]
public sealed class TestTubeChannelsController : ControllerBase
{
    public static readonly Guid Open = Guid.Parse("7e570000-0000-4000-8000-000000000001");
    public static readonly Guid Locked = Guid.Parse("7e570000-0000-4000-8000-000000000002");

    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        encryptsAtRest = false,
        items = new object[]
        {
            new
            {
                id = Locked, name = "Locked channel", canWrite = false,
                videos = new object[] { Row("7e570000-0000-4000-8000-000000000011", "Shared talk", published: true, isReference: true) },
            },
            new
            {
                id = Open, name = "Open channel", canWrite = true,
                videos = new object[]
                {
                    Row("7e570000-0000-4000-8000-000000000012", "Keynote", published: true, isReference: false),
                    Row("7e570000-0000-4000-8000-000000000013", "Unfinished cut", published: false, isReference: false),
                },
            },
        },
        links = new[] { new { rel = "self", href = "/api/test-module/tube-channels", method = "GET" } },
    });

    [HttpHead]
    public IActionResult Head() => Ok();

    private static object Row(string id, string name, bool published, bool isReference) => new
    {
        id = Guid.Parse(id),
        name,
        description = $"About {name}",
        tags = new[] { "demo" },
        published,
        isReference,
        canEdit = false,
        canPlay = false,
        canRemoveFromChannel = false,
        durationSeconds = 0,
        links = Array.Empty<object>(),
    };
}
