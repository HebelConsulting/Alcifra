using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Principals;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// A user's identity aliases: further addresses they are known by, on the tenant's identity domains (ADR 1015).
/// </summary>
/// <remarks>
/// <para>
/// An alias does not sign in; it says who an address NAMES. Strict certificate binding accepts a certificate naming
/// one, and a certificate filed for an earlier address keeps its holder after the account's address changes. So adding
/// one widens whose certificates count as this user's, which is why it is an administrator's act (<c>CanManageUsers</c>),
/// confirmed at once because the administrator vouches for it, and audited in both directions.
/// </para>
/// <para>
/// A collection under the user, reached by the user resource's <c>identity-aliases</c> rel: GET lists, POST adds (one
/// rel, the method says which, ADR 0719), and each alias carries its own <c>remove</c>.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/users/{userId:guid}/identity-aliases")]
[Authorize]
public class UserIdentityAliasesController(
    AlcifraDbContext dbContext,
    Principals.UserAccessService access,
    Concurrency.UserIdentityAliasVerbs aliases,
    IAuditRecorder audit,
    TimeProvider time,
    ILogger<UserIdentityAliasesController> logger) : ControllerBase
{
    // Plain mutable classes — XmlSerializer (ADRs 0189/0190).
    public class IdentityAliasResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Address { get; set; } = string.Empty;

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? ConfirmedAt { get; set; }

        /// <summary>The precondition of its <c>remove</c>.</summary>
        public string ETag { get; set; } = string.Empty;
    }

    public class IdentityAliasesResource : HypermediaResource
    {
        public List<IdentityAliasResource> Aliases { get; set; } = [];
    }

    public class AddIdentityAliasRequest
    {
        public string Address { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid userId, CancellationToken cancellationToken)
    {
        if (!await access.CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!await dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return NotFound();
        }

        var rows = await dbContext.UserIdentityAliases
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.NormalizedAddress)
            .ToListAsync(cancellationToken);
        var collection = $"/api/users/{userId}/identity-aliases";
        return Ok(new IdentityAliasesResource
        {
            Aliases = [.. rows.Select(ToResource)],
            Links = [new Link("self", collection, "GET"), new Link("add", collection, "POST")],
        });
    }

    // Standing convention: every GET action gets its own HEAD action.
    [HttpHead]
    public async Task<IActionResult> Head(Guid userId, CancellationToken cancellationToken) =>
        !await access.CanManageUsersAsync(cancellationToken) ? Forbid()
        : await dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken) ? Ok()
        : NotFound();

    [HttpPost]
    public async Task<IActionResult> Add(Guid userId, [FromBody] AddIdentityAliasRequest request, CancellationToken cancellationToken)
    {
        if (!await access.CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken) is not { } user)
        {
            return NotFound();
        }

        var address = request.Address?.Trim() ?? string.Empty;
        if (address.Split('@') is not [{ Length: > 0 }, var domain] || !MailDomainName.IsWellFormed(domain))
        {
            throw new InvalidUserEmailException(address);
        }

        // On one of the tenant's VERIFIED identity domains: the domain is what the tenant vouches for.
        var normalizedDomain = domain.TrimEnd('.').ToUpperInvariant();
        if (!await dbContext.TenantMailDomains.AnyAsync(
                d => d.NormalizedDomain == normalizedDomain && d.IsIdentity && d.VerifiedAt != null, cancellationToken))
        {
            throw new IdentityAliasDomainNotIdentityException(address);
        }

        // An address names at most one person (both directions are also SaveChanges' invariant; this makes the
        // refusal a sentence rather than a 500).
        var normalized = address.ToUpperInvariant();
        if (await dbContext.Users.AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken)
            || await dbContext.UserIdentityAliases.AnyAsync(a => a.NormalizedAddress == normalized, cancellationToken))
        {
            throw new IdentityAliasConflictException(address);
        }

        var now = time.GetUtcNow();
        var alias = new UserIdentityAlias
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Address = address,
            CreatedAt = now,
            ConfirmedAt = now,   // the administrator vouches for it (ADR 1015); self-service confirms by e-mail link later
        };
        dbContext.UserIdentityAliases.Add(alias);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Information: a security-relevant success — it widens whose certificates count as this user's.
        logger.LogInformation("Identity alias {Address} added to user {UserId}", address, user.Id);
        await audit.RecordAsync(AuditActions.UserIdentityAliasAdded, "User", user.Id, user.DisplayName,
            $"Added the identity alias '{address}' to {user.Email}.", cancellationToken: cancellationToken);

        aliases.EmitETag(Response, alias);
        return Created($"/api/users/{userId}/identity-aliases", ToResource(alias));
    }

    [HttpDelete("{aliasId:guid}")]
    public async Task<IActionResult> Remove(Guid userId, Guid aliasId, CancellationToken cancellationToken)
    {
        if (!await access.CanManageUsersAsync(cancellationToken))
        {
            return Forbid();
        }

        if (await dbContext.UserIdentityAliases.FirstOrDefaultAsync(a => a.Id == aliasId && a.UserId == userId, cancellationToken)
            is not { } alias)
        {
            return NotFound();
        }

        aliases.RequireIfMatch(Request);
        await aliases.MutateAsync(Request, alias,
            apply: () =>
            {
                dbContext.UserIdentityAliases.Remove(alias);
                return Task.CompletedTask;
            },
            afterCommit: async () =>
            {
                // Information: certificates naming this address no longer count as the user's under Strict binding.
                logger.LogInformation("Identity alias {Address} removed from user {UserId}", alias.Address, userId);
                await audit.RecordAsync(AuditActions.UserIdentityAliasRemoved, "User", userId, alias.Address,
                    $"Removed the identity alias '{alias.Address}'.", cancellationToken: cancellationToken);
            },
            touchEntity: false,
            cancellationToken: cancellationToken);

        return NoContent();
    }

    private static IdentityAliasResource ToResource(UserIdentityAlias alias) => new()
    {
        Id = alias.Id,
        Address = alias.Address,
        ConfirmedAt = alias.ConfirmedAt,
        ETag = $"\"{alias.ConcurrencyToken}\"",
        Links = [new Link("remove", $"/api/users/{alias.UserId}/identity-aliases/{alias.Id}", "DELETE")],
    };
}
