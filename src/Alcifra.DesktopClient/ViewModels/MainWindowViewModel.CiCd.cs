using Alcifra.ApiClient.CiCd;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// The CI/CD store in the workbench (ADR 1084, owner 2026-11-09): a CI/CD secret met in the Repositories tree shows its
/// targets, key names and variables in the preview at once, its values after one open with the key; the store's New ▸
/// offers a secret through the store's own door, and Edit changes one in a single sealed version.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The seeded mask's name, which a listing row carries (<c>WellKnownMaskSeeder</c>).</summary>
    private const string CiCdSecretMask = "CI/CD Secret";

    /// <summary>Set by the window: asks where to save a file under the suggested name, writes it, false when cancelled.</summary>
    public Func<string, byte[], Task<bool>>? SaveCiCdFile { get; set; }

    /// <summary>Set by the window: shows the editor; true when it saved.</summary>
    public Func<CiCdEntryEditorViewModel, Task<bool>>? ShowCiCdEditor { get; set; }

    private CiCdStoreClient CiCd => new(_api!.Core, PasswordKeyOpener.Instance);

    private bool DetailIsCiCdSecret() =>
        SelectedItem is { IsFolder: false } selected && selected.Id == _selectedDocumentId
        && string.Equals(selected.DocumentType, CiCdSecretMask, StringComparison.Ordinal);

    /// <summary>
    /// The selected secret's preview. A CI/CD store is flat, so the folder on show is its store: its listing carries every
    /// row's plain index, and the entry's own resource the rels its values are opened and saved through.
    /// </summary>
    private async Task ShowCiCdSecretAsync()
    {
        var (documentId, storeId) = (_selectedDocumentId, _currentFolderId);
        Preview.Reset(Strings.Get("PwOpening"));
        try
        {
            var client = CiCd;
            var store = await StoreOfAsync(client, storeId)
                ?? throw new InvalidOperationException(Strings.Get("CiCdNotYours"));
            var row = store.Items.FirstOrDefault(i => i.Id == documentId)
                ?? throw new InvalidOperationException(Strings.Get("CiCdNotYours"));
            var entry = await client.GetEntryAsync(row.Href);
            if (_selectedDocumentId == documentId)
            {
                var preview = new CiCdEntryPreviewViewModel(entry, client, SecretServices)
                {
                    SaveFile = (name, bytes) => SaveCiCdFile?.Invoke(name, bytes) ?? Task.FromResult(false),
                };
                preview.Edit = EditCiCdSecretAsync;
                Preview.ShowCiCdEntry(preview);
            }
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or Alcifra.ApiClient.ApiActionException
            && _selectedDocumentId == documentId)
        {
            Preview.Reset(e.Message);
        }
    }

    /// <summary>A new secret in the CI/CD store <paramref name="storeId"/>, from the editor: nothing is sent before Save.</summary>
    public async Task CreateCiCdSecretAsync(Guid storeId)
    {
        if (_api is null || ShowCiCdEditor is not { } show)
        {
            return;
        }

        var client = CiCd;
        var editor = new CiCdEntryEditorViewModel(null, async (name, content) =>
        {
            var store = await StoreOfAsync(client, storeId) ?? throw new InvalidOperationException(Strings.Get("CiCdNotYours"));
            await client.CreateAsync(store, name, content);
        });
        if (await show(editor))
        {
            Status = string.Format(Strings.Get("CiCdCreated"), editor.Name.Trim());
            await ShowNewChildInTreeAsync(storeId);
            if (_currentFolderId == storeId)
            {
                await LoadFolderContentsAsync(storeId);
            }
        }
    }

    // Edit: the values are opened first (the preview may already hold them), so the version carries the tag of that read.
    private async Task EditCiCdSecretAsync(CiCdEntryPreviewViewModel preview)
    {
        if (ShowCiCdEditor is not { } show)
        {
            return;
        }

        if (preview.Opened is null)
        {
            await preview.OpenCommand.ExecuteAsync(null);
        }

        if (preview.Opened is not { } opened)
        {
            return;
        }

        var client = CiCd;
        var editor = new CiCdEntryEditorViewModel(opened, (_, content) => client.SaveAsync(opened, content));
        if (await show(editor))
        {
            Status = string.Format(Strings.Get("CiCdSaved"), opened.Entry.Name);
            await ShowCiCdSecretAsync();
        }
    }

    // The CI/CD store with this id, as its listing shows it to the caller: null when it is not one they can decrypt.
    private static async Task<PasswordFolderInfo?> StoreOfAsync(CiCdStoreClient client, Guid? storeId) =>
        (await client.ListStoresAsync()).FirstOrDefault(s => s.Id == storeId) is { } summary
            ? await client.GetStoreAsync(summary.Href)
            : null;
}
