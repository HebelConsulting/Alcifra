namespace Alcifra.Infrastructure.Storage;

/// <summary>
/// The bucket prefix changed and no legacy prefix says where the existing objects are (ADR 1000): refuses to start.
/// </summary>
/// <remarks>
/// Starting would serve every tenant an empty archive at 200 (each read misses the new bucket and nothing falls back),
/// which reads as lost data rather than a missing setting. Refusing names the setting instead.
/// </remarks>
public sealed class TenantBucketsStrandedException(string bucketPrefix, string formerPrefix, int tenants)
    : InvalidOperationException(
        $"ObjectStorage:BucketPrefix is '{bucketPrefix}', but {tenants} tenant(s) have their objects in '{formerPrefix}-<tenant>' "
        + $"buckets and no '{bucketPrefix}-<tenant>' bucket. Set ObjectStorage:LegacyBucketPrefix to '{formerPrefix}' so "
        + "reads fall back while 'alcifra storage migrate' moves them, or set BucketPrefix back to it.")
{
    /// <summary>The prefix the objects are still under.</summary>
    public string FormerPrefix { get; } = formerPrefix;
}
