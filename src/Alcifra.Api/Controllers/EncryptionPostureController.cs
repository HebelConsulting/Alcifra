using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Encryption;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The caller's tenant's encryption posture, read-only, for any signed-in user of the tenant (core #1864, ADR 0955):
/// the mode's name and its four membership answers. Reached by the <c>encryption</c> rel on <c>/me</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own resource, beside <c>readerCertificates</c>, rather than a field elsewhere.</b> Not on the offline-cache
/// policy: that is an administrator's editable setting with its own tag and PUT, and a value no PUT can change does
/// not belong in a body the PUT replaces. Not inline on <c>/me</c>: that is the user's own account, and the posture is
/// the tenant's — installation configuration (ADR 0813), not a property of the person.
/// </para>
/// <para>
/// <b>The four answers come from <see cref="EncryptionModes"/>' membership questions, never from comparing modes</b>
/// (ADR 0834): a client must not infer "Strict is stronger than Storage" from the name, so the answers it acts on are
/// spelled out. The name is for a person reading a support report.
/// </para>
/// <para>
/// <b>Nothing secret.</b> The mode is behaviour every user of the tenant already observes (envelopes arrive, doors
/// refuse plaintext); no address, key, key generation or licence state rides here.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/encryption")]
[Authorize]
public class EncryptionPostureController(
    AlcifraDbContext dbContext,
    ICurrentTenantAccessor currentTenant,
    ICurrentUserAccessor currentUser,
    EncryptionModes modes) : ControllerBase
{
    public class EncryptionPostureResource : HypermediaResource
    {
        /// <summary>The mode's name (<c>None</c>, <c>Storage</c>, <c>Strict</c>, …) — NOT an ordered scale.</summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>Content is wrapped in object storage.</summary>
        public bool WrapsAtRest { get; set; }

        /// <summary>No door serves readable bytes: previews, ranges, presigned URLs, search snippets.</summary>
        public bool RefusesPlaintextDoors { get; set; }

        /// <summary>A content read arrives as an envelope addressed to the reader's certificates.</summary>
        public bool DeliversEnvelopes { get; set; }

        /// <summary>IMAP serves messages enveloped to the recipient's certificate.</summary>
        public bool EnvelopesMail { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (await ReadAsync(cancellationToken) is not { } posture)
        {
            return Forbid();
        }

        Response.Headers.ETag = Tag(posture);
        return Ok(posture);
    }

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        if (await ReadAsync(cancellationToken) is not { } posture)
        {
            return Forbid();
        }

        Response.Headers.ETag = Tag(posture);
        return NoContent();
    }

    /// <summary>Null for a principal that is not a user of a tenant (a service account, a platform administrator).</summary>
    private async Task<EncryptionPostureResource?> ReadAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentTenant.TenantId is not { } tenantId)
        {
            return null;
        }

        var name = await dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        return Describe(modes, name);
    }

    /// <summary>The posture of <paramref name="tenantName"/>, every answer asked of <paramref name="modes"/>.</summary>
    public static EncryptionPostureResource Describe(EncryptionModes modes, string tenantName) => new()
    {
        Mode = modes.ModeFor(tenantName).ToString(),
        WrapsAtRest = modes.WrapsAtRest(tenantName),
        RefusesPlaintextDoors = modes.RefusesPlaintextDoors(tenantName),
        DeliversEnvelopes = modes.DeliversEnvelopes(tenantName),
        EnvelopesMail = modes.EnvelopesMail(tenantName),
        Links = [new Link("self", "/api/me/encryption", "GET")],
    };

    // A tag of the content: the mode is configuration with no row and no concurrency token, so what identifies this
    // representation is what it says. It changes exactly when the configured mode does.
    private static string Tag(EncryptionPostureResource posture) =>
        $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{posture.Mode}|{posture.WrapsAtRest}|{posture.RefusesPlaintextDoors}|{posture.DeliversEnvelopes}|{posture.EnvelopesMail}")))[..16]}\"";
}
