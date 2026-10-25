using System.Globalization;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Passwords tab's acts on a row or a folder (ADR 0968), each through the row's own rels and flags under the ETag it
/// was listed with: rename (the name is in the clear), move within the store (an entry is sealed to its STORE's readers,
/// so it never leaves it), delete (to the recycle bin), a new folder, the one-PIN re-seal, and the import of a password
/// manager's CSV export, sealed on this machine.
/// </summary>
public sealed class PasswordStoreActions(PasswordsTabViewModel tab)
{
    private PasswordDialogs Dialogs => tab.Dialogs;

    public async Task RenameAsync(PasswordItemRowViewModel row)
    {
        if (tab.Work is not { } work || !row.Item.CanRename || Dialogs.AskName is not { } ask
            || await ask(Strings.Get("RibbonRename"), Strings.Get("RenameNewName"), row.Name) is not { Length: > 0 } name || name == row.Name)
        {
            return;
        }

        await ActAsync(() => work.Client.RenameAsync(row.Item.Links, row.Name, row.Item.ETag, name), row.Item.Id);
    }

    public async Task MoveAsync(PasswordItemRowViewModel row)
    {
        if (tab.Work is not { } work || !row.Item.CanMove || Dialogs.PickFolder is not { } pick || tab.SelectedNode is not { } node)
        {
            return;
        }

        IReadOnlyList<PasswordFolderChoice> choices;
        try
        {
            choices = await FoldersOfAsync(work, node.Store, row);
        }
        catch (Exception e) when (PasswordsTabViewModel.IsReported(e))
        {
            tab.Failed(e);
            return;
        }

        if (await pick(choices) is { } target && target.Id != tab.SelectedNode?.Folder?.Id)
        {
            await ActAsync(() => work.Client.MoveAsync(row.Item.Links, row.Name, row.Item.ETag, target), select: null);
        }
    }

    public async Task DeleteAsync(PasswordItemRowViewModel row)
    {
        if (tab.Work is not { } work || !row.Item.CanDelete
            || !await Dialogs.ConfirmAsync(Strings.Get("RibbonDelete"), string.Format(CultureInfo.CurrentCulture, Strings.Get("PwDeleteConfirm"), row.Name), Strings.Get("RibbonDelete")))
        {
            return;
        }

        await ActAsync(() => work.Client.DeleteAsync(row.Item.Links, row.Name, row.Item.ETag), select: null);
    }

    public async Task NewFolderAsync(PasswordFolderInfo folder)
    {
        if (tab.Work is not { } work || !folder.CanCreateFolders || Dialogs.AskName is not { } ask
            || await ask(Strings.Get("PwNewFolder"), Strings.Get("Name"), string.Empty) is not { Length: > 0 } name)
        {
            return;
        }

        Guid? made = null;
        await ActAsync(async () => made = (await work.Client.CreateFolderAsync(folder, name)).Id, select: null);
        if (made is { } id && tab.SelectedNode?.Children.FirstOrDefault(c => c.Row?.Id == id) is { } child)
        {
            tab.SelectedNode = child;
        }
    }

    /// <summary>
    /// Re-seals the store with ONE PIN: the card session holds the login, so every entry is opened with the same key; one
    /// this key cannot open is reported, one changed meanwhile skipped. The outcome is said in one sentence.
    /// </summary>
    public async Task ResealAsync(PasswordFolderInfo folder)
    {
        if (tab.Work is not { } work || !folder.CanReseal)
        {
            return;
        }

        PasswordKeyOpener.UserAsked();
        try
        {
            var store = folder.IsStore ? folder : await work.Client.GetFolderAsync(folder.Links.Href("store")
                ?? throw new InvalidOperationException($"'{folder.Name}' advertised no 'store' rel (ADR 0543)."));
            var outcome = await work.ResealAsync(store);
            await Dialogs.InformAsync(Strings.Get("PwReseal"), PasswordNotices.Describe(outcome));
        }
        catch (Exception e) when (PasswordsTabViewModel.IsReported(e))
        {
            tab.Failed(e);
        }

        await tab.ReloadFolderAsync();
    }

    /// <summary>
    /// Imports a password manager's CSV export into the folder: read and sealed on this machine, the conflicting names
    /// asked about one by one (skip, a new version, keep both), and the plaintext file offered for deletion afterwards.
    /// </summary>
    public async Task ImportAsync(PasswordFolderInfo folder)
    {
        if (tab.Work is not { } work || !folder.CanCreateEntries || Dialogs.PickExport is not { } pick || await pick() is not { } export)
        {
            return;
        }

        PasswordKeyOpener.UserAsked();
        IReadOnlyList<string>? filed;
        PasswordImportPreview preview;
        try
        {
            preview = await work.PrepareImportAsync(folder, export.Text);
            filed = await work.ImportAsync(folder, preview, conflict => Dialogs.AskConflict?.Invoke(conflict) ?? Task.FromResult<PasswordImportResolution?>(null));
        }
        catch (Exception e) when (PasswordsTabViewModel.IsReported(e))
        {
            tab.Failed(e);
            await tab.ReloadFolderAsync();
            return;
        }

        if (filed is null)
        {
            return;   // cancelled at a conflict: nothing was filed
        }

        var said = string.Format(CultureInfo.CurrentCulture, Strings.Get("PwImportDone"), filed.Count);
        if (preview.Read.Skipped.Count > 0)
        {
            said = $"{said}\n{string.Format(CultureInfo.CurrentCulture, Strings.Get("PwImportSkippedRows"), preview.Read.Skipped.Count)}";
        }

        await Dialogs.InformAsync(Strings.Get("PwImport"), said);
        await tab.ReloadFolderAsync();
        if (export.Path is { } path && await Dialogs.ConfirmAsync(Strings.Get("PwImport"), Strings.Get("PwImportDeleteCsv"), Strings.Get("RibbonDelete"))
            && !(Dialogs.DeleteFile is { } delete && await delete(path)))
        {
            await Dialogs.InformAsync(Strings.Get("PwImport"), Strings.Get("PwImportDeleteFailed"));
        }
    }

    // A change through one of the row's rels, then the folder read again; a refusal is said (a 412 as such).
    private async Task ActAsync(Func<Task> act, Guid? select)
    {
        try
        {
            await act();
        }
        catch (Exception e) when (PasswordsTabViewModel.IsReported(e))
        {
            tab.Failed(e);
        }

        await tab.ReloadFolderAsync(select);
    }

    // Every folder of the store, indented, except the row itself and what is inside it (a folder cannot move into itself).
    private static async Task<IReadOnlyList<PasswordFolderChoice>> FoldersOfAsync(PasswordWork work, PasswordNodeViewModel store, PasswordItemRowViewModel moving)
    {
        var choices = new List<PasswordFolderChoice>();
        var root = store.Href is { } href ? await work.Client.GetFolderAsync(href) : await work.Client.EnsureMyPasswordsAsync();
        await WalkAsync(root, 0);
        return choices;

        async Task WalkAsync(PasswordFolderInfo folder, int depth)
        {
            choices.Add(new PasswordFolderChoice(folder, depth));
            foreach (var child in folder.Items.Where(i => i.IsFolder && i.Id != moving.Item.Id))
            {
                await WalkAsync(await work.Client.GetFolderAsync(child.Href), depth + 1);
            }
        }
    }
}
