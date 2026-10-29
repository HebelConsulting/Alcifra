using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Encryption;
using Alcifra.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// The filing half of sealed module content (ABI 1.20, ADR 0940): a mask that declares its content sealed gets only bytes
/// shaped as a CMS envelope, checked BEFORE anything is stored or tracked, so a refusal leaves nothing behind. The serving
/// half is the storage door's (<c>EncryptingObjectStorageClient</c>), which checks the stored bytes again.
/// </summary>
internal static class ModuleSealedContent
{
    /// <summary>Refuses with <see cref="ModuleContentNotSealedException"/> when the document's mask declares its content
    /// sealed and <paramref name="peek"/> does not describe an envelope. The mask is the replaced document's when one is
    /// named, else <paramref name="maskId"/>.</summary>
    public static async Task RefuseUnsealedAsync(
        AlcifraDbContext dbContext, IServiceProvider? services, Guid? replaceDocumentId, Guid maskId,
        Func<CancellationToken, Task<(byte[] Head, long Length)>> peek, CancellationToken cancellationToken)
    {
        if (services?.GetService(typeof(IModuleMaskDeclarations)) is not IModuleMaskDeclarations { SealedContent.Count: > 0 } declarations)
        {
            return;
        }

        var mask = replaceDocumentId is { } documentId
            ? await dbContext.Documents.Where(d => d.Id == documentId)
                .Join(dbContext.MaskVersions, d => d.MaskVersionId, v => v.Id, (_, v) => (Guid?)v.MaskId)
                .FirstOrDefaultAsync(cancellationToken)
            : maskId;
        if (mask is not { } id || !declarations.SealedContent.Contains(id))
        {
            return;
        }

        var (head, length) = await peek(cancellationToken);
        if (!CmsEnvelopeShape.IsEnvelope(head, length))
        {
            throw new ModuleContentNotSealedException();
        }
    }

    /// <summary>The peek of bytes a module passed in.</summary>
    public static Func<CancellationToken, Task<(byte[] Head, long Length)>> Of(byte[] content) =>
        _ => Task.FromResult((content, (long)content.Length));
}
