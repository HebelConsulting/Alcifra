using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Storage;

// Thrown when a tenant's storage migration is asked for on an installation with no ObjectStorage:LegacyBucketPrefix
// (ADR 1000). The rel is not advertised then, so only a composed request reaches this.
public sealed class BucketMigrationNotConfiguredException : StorageException
{
    public BucketMigrationNotConfiguredException()
        : base("BUCKET_MIGRATION_NOT_CONFIGURED", StatusCodes.Status409Conflict,
            "This installation has no legacy bucket prefix configured, so there is nothing to migrate.")
    {
    }
}
