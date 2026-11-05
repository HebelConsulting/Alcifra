using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.Bookmarks;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The bookmark files a browser keeps in step with (ADR 1062). A file is a document: its content is read and written by the
/// document's own version links, and a change is one save whose confirmation carries the tag the browser's copy came from.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/bookmarks")]
[Authorize]
public class BookmarksController(BookmarkFiles files, ICurrentUserAccessor currentUser, ICurrentTenantAccessor currentTenant) : ControllerBase
{
    public class BookmarkFileResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public bool IsPersonal { get; set; }
        public bool CanEdit { get; set; }
        public bool HasContent { get; set; }
        public string ETag { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? Ok((await files.ListAsync(userId, cancellationToken)).Select(Resource))
            : Ok(Array.Empty<BookmarkFileResource>());   // a service account keeps no browser

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public IActionResult Head() => NoContent();

    /// <summary>The caller's personal file, made at their first sync; asking again finds it.</summary>
    [HttpPost]
    public async Task<IActionResult> EnsurePersonal(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || currentTenant.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var (file, created) = await files.EnsurePersonalAsync(userId, tenantId, cancellationToken);
        return created ? Created($"/api/documents/{file.Id}", Resource(file)) : Ok(Resource(file));
    }

    private static BookmarkFileResource Resource(BookmarkFile file) => new()
    {
        Id = file.Id,
        Name = file.Name,
        FolderName = file.FolderName,
        IsPersonal = file.IsPersonal,
        CanEdit = file.CanEdit,
        HasContent = file.CurrentVersionId is not null,
        ETag = $"\"{file.ConcurrencyToken}\"",
        Links =
        [
            new Link("document", $"/api/documents/{file.Id}", "GET"),
            .. file.CurrentVersionId is { } current ? [new Link("currentVersion", $"/api/documents/{file.Id}/versions/{current}", "GET")] : Array.Empty<Link>(),
            .. file.CanEdit ? [new Link("versions", $"/api/documents/{file.Id}/versions", "POST")] : Array.Empty<Link>(),
        ],
    };
}
