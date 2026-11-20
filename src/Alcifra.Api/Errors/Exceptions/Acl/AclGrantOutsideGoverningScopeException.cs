using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Acl;

/// <summary>
/// An ACL grant written on a document that does not break inheritance (#1921): rights resolve at the GOVERNING SCOPE
/// (ADR 0183 — the document itself when it breaks inheritance, else the nearest ancestor that does), so an entry there
/// would answer 2xx and grant nothing. Refused, naming the scope to grant on instead, so every accepted write means
/// what it says.
/// </summary>
public sealed class AclGrantOutsideGoverningScopeException(string scopeName, Guid scopeId)
    : AclException(
        "ACL_GRANT_OUTSIDE_GOVERNING_SCOPE",
        StatusCodes.Status409Conflict,
        $"This document inherits its rights: a grant here would never be read. Break inheritance first, or grant on "
        + $"'{scopeName}' ({scopeId}), the document whose entries govern it.")
{
    /// <summary>The document whose ACL entries govern the target, which the caller should grant on instead.</summary>
    public Guid ScopeId { get; } = scopeId;
}
