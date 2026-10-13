using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Tenant;

public sealed class InvalidOfflineCachePolicyException : TenantException
{
    public InvalidOfflineCachePolicyException()
        : base("INVALID_OFFLINE_CACHE_POLICY", StatusCodes.Status400BadRequest,
            "The offline-cache policy needs a known unlock mode and refresh mode, and a background unlock time of 1 to 1440 minutes.")
    {
    }
}
