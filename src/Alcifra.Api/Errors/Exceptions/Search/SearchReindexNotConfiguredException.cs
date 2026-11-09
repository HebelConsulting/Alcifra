using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Search;

// A search-index rebuild asked of an installation without OpenSearch (ADR 1081): nothing would ever run it, so the
// request is refused instead of queued.
public sealed class SearchReindexNotConfiguredException : SearchException
{
    public SearchReindexNotConfiguredException()
        : base("SEARCH_REINDEX_NOT_CONFIGURED", StatusCodes.Status409Conflict,
            "This installation has no search engine configured, so there is no index to rebuild.")
    {
    }
}
