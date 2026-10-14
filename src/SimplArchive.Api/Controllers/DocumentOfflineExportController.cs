using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The offline export (ADR 0927; #1759): what the caller may read of a document or folder, as an archive in the export
/// format (ADR 0347), for the phone's offline copy, which merges it in. One producer of the format: the regular
/// exporter, given the caller as its audience.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>CanExport</c>:</b> the gate is the tenant's offline policy (ADR 0923), and the archive holds only what
/// the caller could read online anyway: documents they may see, content they may read, the current version as they
/// may read it, the visible folder chain above the anchor, no permissions.
/// </para>
/// <para>
/// <b>Refused on a tenant that refuses plaintext doors</b>, as the regular export is, until the strict tier's
/// envelopes in the export arrive (#1759, slice 4). <b>Every export is recorded</b> in the audit trail (owner decision
/// 2026-10-14): data leaving onto a device is worth a line.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class DocumentOfflineExportController(
    SimplArchiveDbContext dbContext,
    DocumentAccessService access,
    RepositoryExporter exporter,
    StrictEnvelopeDelivery strictEnvelopes,
    ICurrentUserAccessor currentUser,
    ICurrentTenantAccessor currentTenant,
    IAuditRecorder audit) : ControllerBase
{
    [HttpGet("offline-export")]
    public async Task<IActionResult> OfflineExport(Guid documentId, CancellationToken cancellationToken)
    {
        var anchor = await GateAsync(documentId, cancellationToken);
        if (anchor is null)
        {
            return NotFound();
        }

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"offline-{documentId:N}.zip\"";
        // ZipArchive writes synchronously; allowed for this streamed body, as for the regular export.
        if (HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } bodyControl)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        await exporter.ExportAsync(
            documentId,
            new RepositoryExportFilters(null, null, null, null, ExportVersionSelection.CurrentForCaller, null),
            includePermissions: false,
            Response.Body,
            new OfflineExportAudience(currentUser.UserId, access.GetCallerRightsForManyAsync),
            cancellationToken);
        await audit.RecordAsync(AuditActions.DocumentOfflineExported, "Document", anchor.Value.Id, anchor.Value.Name, "Offline export", cancellationToken: cancellationToken);
        return new EmptyResult();
    }

    [HttpHead("offline-export")]
    public async Task<IActionResult> OfflineExportHead(Guid documentId, CancellationToken cancellationToken) =>
        await GateAsync(documentId, cancellationToken) is null ? NotFound() : Ok();

    // The policy, the strict tier, and an anchor the caller may see (an unseen one is not named: 404).
    private async Task<(Guid Id, string Name)?> GateAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var allowed = await dbContext.Tenants.Where(t => t.Id == currentTenant.TenantId).Select(t => t.OfflineCacheAllowed).SingleOrDefaultAsync(cancellationToken);
        if (!allowed)
        {
            throw new OfflineExportNotAllowedException();
        }

        await strictEnvelopes.RefuseIfStrictAsync("an offline export", cancellationToken);
        var anchor = await dbContext.Documents.Where(d => d.Id == documentId).Select(d => new { d.Id, d.Name }).SingleOrDefaultAsync(cancellationToken);
        return anchor is not null && (await access.GetCallerRightsAsync(documentId, cancellationToken)).CanSee ? (anchor.Id, anchor.Name) : null;
    }
}
