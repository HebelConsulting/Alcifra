using System.Security.Cryptography;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace Alcifra.Api.Checkouts;

/// <summary>
/// The one place the check-out listing decides "modified" (#530, ADR 0513): the working copy's stash is hashed and
/// compared with the confirmed version's SHA-256, both clients gating Check-in on the answer.
/// </summary>
/// <remarks>
/// <b>A stash this process cannot read leaves the answer UNSAID, not the listing failed</b> (#1859). Without
/// <c>Encryption:ServiceUrl</c> an at-rest-encrypted stash refuses its read (ADR 0953); the row then still says the
/// stash EXISTS — its download link is already omitted by <see cref="OmittableLinks"/> — and <c>isModified</c> stays
/// false. True would send the person to a Check-in that reads the same unreadable stash and answers 503.
/// </remarks>
/// <remarks>
/// <b>No line of its own</b>: the listing presigns the same stash a moment earlier, and <see cref="OmittableLinks"/>
/// has already written the one Warning naming it and the missing setting (ADR 1094). A second line here would be the
/// duplication #1859 was about, so this only notes the fact for the trace.
/// </remarks>
public sealed class CheckoutStash(IObjectStorageClient storage, ILogger<CheckoutStash> logger)
{
    /// <summary>Whether the stash differs from the confirmed version; null when this process cannot read it here.</summary>
    public async Task<bool?> IsModifiedAsync(string stashKey, string versionSha256, CancellationToken cancellationToken)
    {
        try
        {
            await using var stash = await storage.GetObjectAsync(stashKey, cancellationToken);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stash, cancellationToken));
            return !string.Equals(hash, versionSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (EncryptedObjectWithoutEncryptionServiceException)
        {
            logger.LogDebug(
                "The working copy of {ObjectKey} is encrypted at rest and this installation has no "
                + "Encryption:ServiceUrl, so whether it is modified cannot be read here. The check-out is listed with "
                + "its stash counted and its download withheld; the omitted link's Warning is this refusal's line.",
                stashKey);
            return null;
        }
    }
}
