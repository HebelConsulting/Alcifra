namespace SimplArchive.Presentation;

/// <summary>
/// The names the product speaks ON THE WIRE (ADR 0987): fixed, whatever the build or the installation is called. A
/// brand changes what a person reads, never what a client or a server sends, so a white-labelled server answers every
/// client the same. Nothing here may be derived from the product's name.
/// </summary>
public static class WireNames
{
    /// <summary>The product's name in protocol identifiers, e.g. an iCalendar PRODID.</summary>
    public const string Product = "alcifra";

    /// <summary>The WebDAV mount's single path segment; the OS labels a mounted volume with it.</summary>
    public const string WebDavSegment = "webdav";
}
