using Alcifra.ApiClient;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The name a document's bytes are written out of the desktop under: saved, dragged out, or opened in their application.
/// </summary>
/// <remarks>
/// A sealed document arrives DECRYPTED through the content funnel, so naming it by its stored version (<c>.p7m</c>) wrote a
/// PDF as an envelope's file (#2312). Content that was enveloped is named after what it decrypted to; anything else keeps
/// the name it was filed under.
/// </remarks>
internal static class SavedFileName
{
    /// <param name="fileName">The name as filed: the document's name with its version's extension.</param>
    /// <param name="read">The content as the funnel read it (<see cref="ApiCore.ReadNamedContentAsync"/>).</param>
    public static string Of(string fileName, (byte[] Bytes, string ContentType, bool WasEnveloped, string? FileName) read) =>
        read.WasEnveloped ? SealedDocument.OpenedFileName(fileName, read.FileName, read.ContentType) : fileName;

    /// <summary>
    /// The name a save dialog suggests, and the bytes when they had to be read for it. A sealed document is read FIRST,
    /// because only what it decrypts to names it; anything else is read after the dialog, so cancelling downloads nothing.
    /// </summary>
    public static async Task<(string FileName, byte[]? Bytes)> SuggestAsync(string downloadUrl, string fileName)
    {
        if (!fileName.EndsWith(SealedDocument.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return (fileName, null);
        }

        var read = await ApiCore.ReadNamedContentAsync(downloadUrl);
        return (Of(fileName, read), read.Bytes);
    }
}
