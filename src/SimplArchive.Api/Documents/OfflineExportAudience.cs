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
public sealed record OfflineExportAudience(
    Guid? UserId,
    Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, EffectiveRights>>> RightsForMany);
