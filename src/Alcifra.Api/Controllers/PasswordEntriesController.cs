using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.PasswordStores;
using Alcifra.Api.PasswordStores;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Presentation;

namespace Alcifra.Api.Controllers;

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
    PasswordStoreResourceBuilder resources,
    Alcifra.Api.Audience.ContentAudience audience,
    ICurrentUserAccessor currentUser,
    IAuditRecorder audit,
    AlcifraDbContext dbContext) : ControllerBase
{
    /// <summary>
    /// What a client did with a CI/CD secret's opened value (ADR 1084): revealed it, or applied it to a target. Recorded by
    /// key name and target, never a value. The client's word, unlike the delivery the server records itself; offered
    /// (rel <c>cicd-report</c>) only to a member of the store's audience, who alone could have opened it.
    /// </summary>
    [HttpPost("cicd-reports")]
    public async Task<IActionResult> Report(Guid entryId, [FromBody] CiCdReportRequest request, CancellationToken cancellationToken)
    {
        var entry = await VisibleEntryAsync(entryId, cancellationToken);
        var action = request.Action switch
        {
            "revealed" => AuditActions.CiCdSecretRevealed,
            "applied" => AuditActions.CiCdSecretApplied,
            _ => null,
        };
        var index = (await CiCdIndexValues.LoadAsync(dbContext, [entry.Id], cancellationToken)).GetValueOrDefault(entry.Id);
        if (entry.Family != SealedStoreFamily.CiCd || action is null || index is null
            || !(index.SecretKeys ?? []).Concat((index.Variables ?? []).Select(v => CiCdEntryRules.SplitVariable(v)?.Name ?? v))
                .Contains(request.Key, StringComparer.Ordinal)
            || (request.Target is { } target && !(index.Targets ?? []).Contains(target, StringComparer.Ordinal)))
        {
            throw new CiCdReportInvalidException();
        }

        if (await audience.RootOfAsync(entry.StoreId, cancellationToken) is not { } root
            || !await audience.IsMemberAsync(root, currentUser.UserId, cancellationToken))
        {
            return Forbid();
        }

        var detail = request.Target is { } to ? $"Key {request.Key}, target {to}" : $"Key {request.Key}";
        await audit.RecordAsync(action, "Document", entry.Id, entry.Name, detail, cancellationToken: cancellationToken);
        return NoContent();
    }

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

/// <summary>What a client reports doing with a CI/CD secret's value (ADR 1084): <c>revealed</c> or <c>applied</c>, the key, and the target.</summary>
public sealed class CiCdReportRequest
{
    public string Action { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string? Target { get; set; }
}
