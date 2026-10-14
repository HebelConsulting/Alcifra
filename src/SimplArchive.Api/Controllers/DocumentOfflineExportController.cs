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
/// <b>On a tenant that refuses plaintext doors</b> the export runs only where the Encryption Module allows it, with
/// content as envelopes to the caller's certificates and, when the module says so, index data and chat sealed too
/// (ADR 0928). <b>Every export is recorded</b> in the audit trail (owner decision
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
    IAuditRecorder audit,
    Infrastructure.Modules.ModuleOfflineExportPolicy modulePolicy,
    Infrastructure.Encryption.SmimeMessageEnveloper enveloper) : ControllerBase
{
    [HttpGet("offline-export")]
    public async Task<IActionResult> OfflineExport(Guid documentId, CancellationToken cancellationToken)
    {
        var anchor = await GateAsync(documentId, cancellationToken);
        if (anchor is null)
        {
            return NotFound();
        }

        // A tenant that refuses plaintext doors (ADR 0928): content as envelopes to the caller, index data and chat
        // sealed too when the module says so. The module allowed it in the gate; the caller's certificates are required.
        Func<Domain.Documents.DocumentVersion, CancellationToken, Task<byte[]>>? envelope = null;
        Func<byte[], byte[]>? seal = null;
        var oncePerSession = false;
        if (await strictEnvelopes.RefusesPlaintextDoorsAsync(cancellationToken))
        {
            var certificates = await strictEnvelopes.RequireReaderCertificatesAsync(cancellationToken);
            envelope = (version, ct) => strictEnvelopes.EnvelopeAsync(version.ObjectKey, $"{version.Id:N}{Path.GetExtension(version.ObjectKey)}", certificates, ct);
            var policy = await modulePolicy.ForTenantAsync(cancellationToken);
            oncePerSession = policy.ContentOncePerSession;   // ADR 0929: said in the manifest, the device acts on it
            if (policy.SealIndexDataAndChat)
            {
                seal = bytes => enveloper.TryEnvelopeDocument(bytes, "application/json", "sealed.json", null, certificates)
                    ?? throw new Errors.Exceptions.Encryption.ContentCannotBeEnvelopedException();
            }
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
            new OfflineExportAudience(currentUser.UserId, access.GetCallerRightsForManyAsync, envelope, seal, oncePerSession),
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

        // A tenant that refuses plaintext doors offers the export only where its module allows it (ADR 0928).
        if (await strictEnvelopes.RefusesPlaintextDoorsAsync(cancellationToken) && !(await modulePolicy.ForTenantAsync(cancellationToken)).Allowed)
        {
            throw new OfflineExportNotAllowedOnThisTenantException();
        }

        var anchor = await dbContext.Documents.Where(d => d.Id == documentId).Select(d => new { d.Id, d.Name }).SingleOrDefaultAsync(cancellationToken);
        return anchor is not null && (await access.GetCallerRightsAsync(documentId, cancellationToken)).CanSee ? (anchor.Id, anchor.Name) : null;
    }
}
