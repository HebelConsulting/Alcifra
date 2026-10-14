using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Who an offline export is for (ADR 0927): the export then holds only what this caller may read. Documents the
/// caller cannot see are left out; content only where the caller may read it; each document's version is the current
/// one as the caller may read it (ADR 0503, with workflow status-gating, ADR 0300); and the visible folder chain above
/// the anchor travels as metadata, so a merged copy places the anchor where the tree has it.
/// </summary>
/// <param name="UserId">The caller as a user, for "the assigned reviewer reads a gated version"; null for a service account.</param>
/// <param name="RightsForMany">The caller's rights on a batch of documents (DocumentAccessService), passed in so the
/// exporter itself never asks who is calling.</param>
/// <param name="Envelope">On a tenant that refuses plaintext doors (ADR 0928): a version's content as the envelope
/// addressed to the caller's certificates, written in place of the content. Null elsewhere.</param>
/// <param name="Seal">With the module's "seal index data and chat": those two files, sealed to the caller's
/// certificates as one S/MIME artefact. Null when they travel as they are.</param>
/// <param name="ContentResealableOnDevice">With the module's "open documents once per session" (ADR 0929): the device
/// may open each envelope once and keep the content sealed to its copy's key. Said in the manifest; the content
/// itself still travels as the envelope.</param>
public sealed record OfflineExportAudience(
    Guid? UserId,
    Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, EffectiveRights>>> RightsForMany,
    Func<Domain.Documents.DocumentVersion, CancellationToken, Task<byte[]>>? Envelope = null,
    Func<byte[], byte[]>? Seal = null,
    bool ContentResealableOnDevice = false);
