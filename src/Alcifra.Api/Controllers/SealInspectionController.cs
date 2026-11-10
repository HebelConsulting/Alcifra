using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Documents;

namespace Alcifra.Api.Controllers;

/// <summary>
/// What a document's sealed content is and who can open it (#2291): reached by the document's <c>seal-inspection</c> rel,
/// answered by <see cref="SealInspectionService"/>. Read-only; nothing is decrypted and nothing leaves but the description.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/seal-inspection")]
[Authorize]
public class SealInspectionController(DocumentAccessService access, SealInspectionService inspections) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
    {
        await RequireContentAsync(documentId, cancellationToken);
        return Ok(await inspections.InspectAsync(documentId, cancellationToken));
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        await RequireContentAsync(documentId, cancellationToken);
        return NoContent();
    }

    // The same right the content itself needs: whoever may not read it is not told whom it is sealed to.
    private async Task RequireContentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (!(await access.GetCallerRightsAsync(documentId, cancellationToken)).CanReadContent)
        {
            throw DocumentNotFoundException.NotFound();
        }
    }
}
