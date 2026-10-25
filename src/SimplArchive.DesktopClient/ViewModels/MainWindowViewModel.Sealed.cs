using System.Security.Cryptography.X509Certificates;
using Avalonia.Platform.Storage;
using SimplArchive.ApiClient;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Filing a SEALED document (ADR 0975): the file is sealed ON THIS DEVICE to an audience of certificates, and only the
// envelope ever reaches the server — which stores it without a key and serves it as the S/MIME message it is.
//
// The other half of this subject is "Change audience…" (a new version sealed to a different set), which needs the
// author check ADR 0975 states and lands with #1917's `createdByUserId`.
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Seals <paramref name="file"/> to <paramref name="audience"/> and files it in the open folder; answers whether it
    /// was filed.
    /// </summary>
    /// <remarks>
    /// The audience comes from the caller, which is where the dialog lives (ADR 0730: the view supplies what it alone can
    /// ask for). This method owns the folder — the same <c>children</c> address, resolved once, that a dropped file
    /// uses — so a ribbon action and a drop cannot disagree about where a document goes.
    /// </remarks>
    public async Task<bool> CreateSealedDocumentAsync(IStorageFile file, IReadOnlyList<X509Certificate2> audience)
    {
        if (_api is null || _currentFolderId is not { } folderId || _currentFolderLinks?.Href("children") is not { } childrenHref)
        {
            ReportError(Strings.Get("StErrSealedNoFolder"));
            return false;
        }

        if (audience.Count == 0)
        {
            return false;
        }

        // A sealed document's name is the stem; the envelope's extension rides on the version (ADR "Extension off
        // Document.Name"). The real name (with its extension) is carried INSIDE the envelope, so opening it on a
        // device still knows what the file is called.
        var name = Path.GetFileNameWithoutExtension(file.Name);
        Status = string.Format(Strings.Get("StSealingFile"), name);
        try
        {
            byte[] bytes;
            await using (var stream = await file.OpenReadAsync())
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }

            var contentType = DocumentsClient.GuessContentType(file.Name);
            // Sealing is CPU work over the whole document; off the UI thread so a large file is not a frozen window.
            var sealedBytes = await Task.Run(() => SealedDocument.Seal(bytes, contentType, audience, file.Name));
            await _api.Documents.UploadSealedDocumentAsync(childrenHref, name, sealedBytes);
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
            return false;
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrSealed"), name, e.Message));
            return false;
        }

        Status = string.Format(Strings.Get("StSealedFiled"), name);
        await LoadFolderContentsAsync(folderId);
        return true;
    }

    // The DOCUMENT's ETag from the read that filled the pane (ADR 0794): a re-seal carries it as its precondition, and a
    // fresh read just before writing would assert about a version the author never saw.
    private string? _detailDocumentETag;

    /// <summary>
    /// Files the current content again, sealed to a NEW audience (ADR 0975). The author's key opens what is there and this
    /// device seals it again; the server sees only the new envelope — and refuses the act from anybody but the author.
    /// </summary>
    /// <remarks>
    /// The content is opened through the one content funnel, so the audience that opens it is exactly the key a reader
    /// uses. An author who is not themselves in the audience cannot re-seal: there is nothing they could open, and this
    /// says so rather than failing as a decrypt error.
    /// </remarks>
    public async Task<bool> ChangeAudienceAsync(IReadOnlyList<X509Certificate2> audience)
    {
        if (_api is null || audience.Count == 0)
        {
            return false;
        }

        if (_detailDocumentETag is not { Length: > 0 } eTag || string.IsNullOrEmpty(_detailDocumentName))
        {
            ReportError(Strings.Get("StErrResealNoSubject"));
            return false;
        }

        var name = _detailDocumentName;
        Status = string.Format(Strings.Get("StResealingDoc"), name);
        try
        {
            var versionsHref = DetailHref("versions");
            var preview = await _api.Documents.GetPreviewAsync(versionsHref);
            var contentUrl = preview.DownloadUrl ?? preview.PreviewUrl
                ?? throw new ApiActionException(Strings.Get("StErrResealNoContent"));

            // Opened by the author's own key — the funnel asks for the card only when the envelope names it.
            var (bytes, contentType) = await ApiCore.GetContentAsync(contentUrl);
            var sealedBytes = await Task.Run(() => SealedDocument.Seal(bytes, contentType, audience, name));

            await _api.Documents.ResealAsync(versionsHref, eTag, name, sealedBytes);
        }
        catch (EnvelopeStaysSealedException)
        {
            ReportError(Strings.Get("StErrResealNotAudience"));
            return false;
        }
        catch (KeyPromptCancelledException)
        {
            Status = string.Empty;
            return false;
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
            return false;
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrReseal"), name, e.Message));
            return false;
        }

        Status = string.Format(Strings.Get("StResealedDoc"), name);
        await RefreshCommand.ExecuteAsync(null);
        return true;
    }
}
