namespace Alcifra.ModuleAbi;

/// <summary>
/// A root route prefix a module serves WITHOUT a credential (ABI 1.28, core ADR 1038): the video module's PeerTube API,
/// read by anyone. The core resolves the tenant from the request's <c>Host</c> through the module setting named here
/// (per tenant, e.g. <c>tube.host</c> = <c>video.customer.ch</c>), runs the activation gate, acts as the module's
/// principal, and rate-limits per client address. An unknown host is 404. The module's controllers under the prefix
/// carry <c>[AllowAnonymous]</c>; the core authenticates nobody there.
/// </summary>
/// <param name="Prefix">The first path segment, on the core's allowlist like every root prefix.</param>
/// <param name="HostSettingKey">The module setting whose per-tenant value is the public host the prefix is served on.</param>
public sealed record AnonymousRootPrefix(string Prefix, string HostSettingKey);

/// <summary>What a module claims at the root: its credentialed prefixes and its anonymous ones, as one list.</summary>
public static class ModuleRouteClaims
{
    /// <summary>Every root prefix the module claims: the credentialed ones and the anonymous ones.</summary>
    public static IReadOnlyList<string> ClaimedRootPrefixes(this IIndustryModule module) =>
        [.. module.RootRoutePrefixes, .. module.AnonymousRootPrefixes.Select(p => p.Prefix)];
}
