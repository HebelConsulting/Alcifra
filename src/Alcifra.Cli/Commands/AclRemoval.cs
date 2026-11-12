using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Removing a principal's entry on a document (<c>alcifra acl remove</c>): afterwards they hold what inheritance gives
/// them there, and on a document that does not inherit, nothing.
/// </summary>
/// <remarks>
/// <c>revoke</c> of every right cannot do this: it would write an entry granting nothing, which the API refuses
/// (<c>EMPTY_GRANT_NOT_ALLOWED</c>). That stopped the kiosk's Tube seed on 2026-11-12, which takes the module's rights
/// away on a folder that no longer inherits them. The entry is deleted at its own address, under the tag it was read
/// with, like every other write here (#1504).
/// </remarks>
public static class AclRemoval
{
    /// <summary>The rights the removed entry held, or null when the principal held no entry there.</summary>
    public static async Task<AclRights?> RemoveAsync(AlcifraApi api, AclSettings settings, CancellationToken cancellationToken)
    {
        var target = await AclSurface.ResolveAsync(api, settings, cancellationToken);
        var (current, etag) = await AclSurface.CurrentAsync(api, target.Address, cancellationToken);
        if (etag is null)
        {
            return null;
        }

        await api.DeleteWithETagAsync(target.Address, etag, cancellationToken);
        return current;
    }

    /// <summary>Why a revoke that would leave the principal holding nothing is refused, and what to run instead.</summary>
    public static string RevokeAllRefused(AclSettings settings) =>
        $"That would leave {PrincipalName(settings)} holding nothing on {settings.Document}, and the API refuses an entry "
        + $"that grants nothing. To take the whole entry away, run: alcifra acl remove {settings.Document} {PrincipalOption(settings)}";

    internal static string PrincipalName(AclSettings settings) => settings.User ?? settings.Group ?? settings.ServiceAccount!;

    private static string PrincipalOption(AclSettings settings) => settings switch
    {
        { User: { Length: > 0 } user } => $"--user \"{user}\"",
        { Group: { Length: > 0 } group } => $"--group \"{group}\"",
        _ => $"--service-account \"{settings.ServiceAccount}\"",
    };
}
