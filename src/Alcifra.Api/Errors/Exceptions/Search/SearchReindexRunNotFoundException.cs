using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Search;

// A search-index rebuild run that does not exist, or no longer does: finished runs are kept 90 days (ADR 1081).
public sealed class SearchReindexRunNotFoundException : SearchException
{
    public SearchReindexRunNotFoundException()
        : base("SEARCH_REINDEX_RUN_NOT_FOUND", StatusCodes.Status404NotFound, "There is no such search-index rebuild.")
    {
    }
}
