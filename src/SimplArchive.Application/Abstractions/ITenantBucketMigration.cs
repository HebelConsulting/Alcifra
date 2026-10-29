namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Moves a tenant's objects from the bucket under the legacy prefix to the one under the current prefix (ADR 1000).
/// </summary>
/// <remarks>
/// Driven in batches by <c>alcifra storage migrate</c>, so a run reports as it goes (ADR 0999) and can stop anywhere:
/// every step is idempotent, and the cursor is only where to resume, never state the migration depends on.
/// </remarks>
public interface ITenantBucketMigration
{
    /// <summary>Whether a legacy prefix is configured, i.e. whether there is anything this could move.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Copies up to <paramref name="limit"/> objects of the legacy bucket that follow <paramref name="after"/> (in key
    /// order) into the tenant's bucket, and removes each legacy copy that no lock keeps.
    /// </summary>
    Task<TenantBucketMigrationBatch> MigrateBatchAsync(Guid tenantId, string? after, int limit, CancellationToken cancellationToken = default);
}

/// <summary>What one batch did (ADR 1000).</summary>
/// <param name="Copied">Objects copied into the tenant's bucket, with their metadata, retention date and legal hold.</param>
/// <param name="AlreadyPresent">Objects the tenant's bucket already held (a newer write, or an earlier run): not copied.</param>
/// <param name="Removed">Legacy copies removed.</param>
/// <param name="Retained">Legacy copies a retention date or legal hold still keeps; a later run removes them.</param>
/// <param name="RetainedUntil">The latest retention date among <paramref name="Retained"/>, when one is known.</param>
/// <param name="Next">The cursor to resume after, or null when the legacy bucket has nothing further.</param>
public sealed record TenantBucketMigrationBatch(
    int Copied, int AlreadyPresent, int Removed, int Retained, DateTimeOffset? RetainedUntil, string? Next);
