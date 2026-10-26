using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.PasswordStores;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Audience;

/// <summary>
/// The sign a listing row carries for encrypted content (ADR 0983), so the Repositories tab and tile can mark what the
/// caller can see but not open — before they open it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="You"/>: the caller is in the effective audience and, for content, the current version is sealed to one of
/// their certificates. <see cref="NotYou"/>: the caller is not in the effective audience. <see cref="NotYetYou"/>: the
/// caller is in it, but the current version is not sealed to them yet (they enrolled a key, or joined a group, since the
/// last re-seal) — the next re-seal resolves it. A store or folder has no content of its own, so it is only ever
/// <see cref="You"/> or <see cref="NotYou"/>. Null on a row that is not encrypted content.
/// </para>
/// <para>
/// The recipients come from <see cref="PasswordStoreSealing.RecipientsAsync"/>, remembered per confirmed version for the
/// process, so a listing reads each envelope's header once in the life of the server rather than once per request.
/// </para>
/// </remarks>
public sealed class EncryptedForCaller(
    SimplArchiveDbContext dbContext,
    ContentAudience audience,
    PasswordStoreSealing sealing,
    ICurrentUserAccessor currentUser)
{
    public const string You = "you";

    public const string NotYou = "not-you";

    public const string NotYetYou = "not-yet-you";

    /// <summary>The sign of each row that is encrypted content; rows that are not are absent from the answer.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ForRowsAsync(
        IEnumerable<(Guid Id, Guid? MaskId)> rows, CancellationToken cancellationToken)
    {
        var signs = new Dictionary<Guid, string>();
        foreach (var (id, maskId) in rows)
        {
            if (maskId is not { } mask || !IsEncryptedContent(mask))
            {
                continue;
            }

            if (await SignAsync(id, mask, cancellationToken) is { } sign)
            {
                signs[id] = sign;
            }
        }

        return signs;
    }

    private static bool IsEncryptedContent(Guid maskId) =>
        maskId == WellKnownMaskIds.SealedDocument || PasswordStoreTree.KindOf(maskId) is not null;

    private async Task<string?> SignAsync(Guid documentId, Guid maskId, CancellationToken cancellationToken)
    {
        if (await audience.RootOfAsync(documentId, cancellationToken) is not { } root)
        {
            return null;
        }

        var me = (await audience.EffectiveAsync(root, cancellationToken)).FirstOrDefault(m => m.UserId == currentUser.UserId);
        if (me is null)
        {
            return NotYou;
        }

        // Only content has recipients: a store or a folder is "you" as soon as the caller is a member.
        if (maskId != WellKnownMaskIds.SealedDocument && maskId != WellKnownMaskIds.PasswordEntry)
        {
            return You;
        }

        var currentVersionId = await dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.CurrentVersionId).FirstOrDefaultAsync(cancellationToken);
        if (await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, documentId, currentVersionId, cancellationToken)
            is not { Status: DocumentVersionStatus.Confirmed } version)
        {
            return You;
        }

        var recipients = await sealing.RecipientsAsync(version, cancellationToken);
        var mine = me.CertificatePems.Select(pem => X509Certificate2.CreateFromPem(pem)).ToList();
        return recipients.Any(r => mine.Any(r.Is)) ? You : NotYetYou;
    }
}
