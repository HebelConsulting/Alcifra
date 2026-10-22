namespace SimplArchive.Api.Errors.Exceptions.OfflineArchives;

// Area base for the sealed offline archive (.simplarchive, ADRs 0960/0961), so a caller can catch the whole family.
public abstract class OfflineArchiveException : ApiException
{
    protected OfflineArchiveException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
