using Avalonia.Platform.Storage;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Encrypted content from the Repositories tab (ADR 0983, superseding ADR 0975's certificate picking): a Sealed document
// is filed to an AUDIENCE of people and groups declared first, and its audience — or a store's — is changed by its
// managers and re-sealed in the same act. The dialogs are the view's (ADR 0730); this partial owns the folder, the
// sealing and the requests.
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Begins a Sealed document for <paramref name="file"/> in the open folder: creates it (the caller is its first
    /// manager) and answers its draft, whose audience the view's editor picks from; null when nothing was begun.
    /// </summary>
    public async Task<SealedDocumentDraft?> BeginSealedDocumentAsync(IStorageFile file)
    {
        if (_api is null || _currentFolderLinks?.Href("children") is not { } childrenHref)
        {
            ReportError(Strings.Get("StErrSealedNoFolder"));
            return null;
        }

        // A sealed document's name is the stem; the envelope's extension rides on the version, and the real name (with
        // its extension) travels INSIDE the envelope, so opening it on a device still knows what the file is called.
        var name = Path.GetFileNameWithoutExtension(file.Name);
        try
        {
            return await _api.Documents.BeginSealedDocumentAsync(childrenHref, name);
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
            return null;
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrSealed"), name, e.Message));
            return null;
        }
    }

    /// <summary>Takes a begun Sealed document back out: the user cancelled the audience.</summary>
    public Task AbandonSealedDocumentAsync(SealedDocumentDraft draft) =>
        _api?.Documents.AbandonSealedDocumentAsync(draft) ?? Task.CompletedTask;

    /// <summary>
    /// Finishes a begun Sealed document: declares <paramref name="audience"/>, seals <paramref name="file"/> on this device
    /// to the audience's certificates as the server answers them, and files it; answers whether it was filed.
    /// </summary>
    public async Task<bool> CompleteSealedDocumentAsync(SealedDocumentDraft draft, IStorageFile file, IReadOnlyList<AudienceChange> audience)
    {
        if (_api is null || _currentFolderId is not { } folderId)
        {
            await AbandonSealedDocumentAsync(draft);
            return false;
        }

        Status = string.Format(Strings.Get("StSealingFile"), draft.Name);
        try
        {
            byte[] bytes;
            await using (var stream = await file.OpenReadAsync())
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }

            // Sealing is CPU work over the whole document; off the UI thread so a large file is not a frozen window.
            await Task.Run(() => _api.Documents.CompleteSealedDocumentAsync(
                draft, audience, bytes, DocumentsClient.GuessContentType(file.Name), file.Name));
        }
        catch (SealedDocumentAudienceRequiredException)
        {
            ReportError(Strings.Get("AudNobodyHasAKey"));
            return false;
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
            return false;
        }
        catch (Exception e)
        {
            ReportError(string.Format(Strings.Get("StErrSealed"), draft.Name, e.Message));
            return false;
        }

        Status = string.Format(Strings.Get("StSealedFiled"), draft.Name);
        await LoadFolderContentsAsync(folderId);
        return true;
    }

    /// <summary>The name of the item the details pane shows, for a dialog's heading.</summary>
    public string DetailDocumentName => _detailDocumentName ?? string.Empty;

    // The DOCUMENT's ETag from the read that filled the pane (ADR 0794): the audience is saved under the tag its own read
    // carried, and the re-seal under the one that save answered.
    private string? _detailDocumentETag;

    /// <summary>The audience of the item the details pane shows (a Sealed document, or a store's item); null when it has none.</summary>
    public async Task<ContentAudienceInfo?> LoadDetailAudienceAsync()
    {
        if (_api is null || _detailLinks?.Href("audience") is not { } href)
        {
            return null;
        }

        try
        {
            return await new ContentAudienceClient(_api.Core).GetAsync(href);
        }
        catch (Exception e) when (e is ApiActionException or HttpRequestException)
        {
            ReportError(e.Message);
            return null;
        }
    }

    /// <summary>
    /// Saves a changed audience and re-seals in the same act (ADR 0983): a Sealed document is opened with the caller's key
    /// and sealed to the new audience; a store's entries are re-sealed by the store client. An administrator appointing a
    /// manager only saves — they hold no key, and the next member's re-seal catches a new member up.
    /// </summary>
    public async Task<bool> SaveAudienceAsync(ContentAudienceInfo seen, IReadOnlyList<AudienceChange> audience)
    {
        if (_api is null)
        {
            return false;
        }

        var name = _detailDocumentName ?? string.Empty;
        Status = string.Format(Strings.Get("AudSaving"), name);
        try
        {
            if (!seen.CanManage)
            {
                await new ContentAudienceClient(_api.Core).SaveAsync(seen, audience);
            }
            else if (seen.IsStore)
            {
                var passwords = new PasswordStoreClient(_api.Core);
                var (stores, _) = await passwords.ListStoresAsync();
                var store = await passwords.GetFolderAsync(stores.Single(s => s.Id == seen.DocumentId).Href);
                var (_, resealed) = await passwords.ChangeAudienceAsync(store, seen, audience);
                if (resealed is { Failed.Count: > 0 } failed)
                {
                    ReportError(string.Format(Strings.Get("AudResealFailed"), failed.Failed.Count));
                }
            }
            else
            {
                await _api.Documents.ChangeSealedAudienceAsync(seen, audience, DetailHref("versions"), name);
            }
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

        Status = string.Format(Strings.Get("AudSaved"), name);
        await RefreshCommand.ExecuteAsync(null);
        return true;
    }
}
