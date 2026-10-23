using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.PasswordStores;
using SimplArchive.Api.PasswordStores;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// One password entry (ADR 0964): its sealed content's address, its ETag (the If-Match of a new version) and whether it
/// needs sealing again. A new version goes through the document's <c>versions</c> rel; the finalize checks the envelope.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/password-entries/{entryId:guid}")]
[Authorize]
public class PasswordEntriesController(
    DocumentAccessService access,
    PasswordStoreTree tree,
    PasswordStoreResourceBuilder resources) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await VisibleEntryAsync(entryId, cancellationToken);
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(entry.ConcurrencyToken);
        return Ok(await resources.EntryAsync(entry, cancellationToken));
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await VisibleEntryAsync(entryId, cancellationToken);
        Response.Headers.ETag = PasswordStoreResourceBuilder.Tag(entry.ConcurrencyToken);
        return NoContent();
    }

    private async Task<PasswordItem> VisibleEntryAsync(Guid entryId, CancellationToken cancellationToken) =>
        await tree.FindAsync(entryId, cancellationToken) is { Kind: PasswordItemKind.Entry } entry
        && (await access.GetCallerRightsAsync(entry.Id, cancellationToken)).CanSee
            ? entry
            : throw new NotAPasswordStoreItemException();
}
