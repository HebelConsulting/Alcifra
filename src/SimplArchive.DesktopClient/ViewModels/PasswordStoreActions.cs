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

        if (await pick(choices) is { } target && target.Id != tab.SelectedNode?.Folder?.Id && tab.SelectedNode?.Folder is { } from)
        {
            // Into another store, the moved entries are sealed to its audience in the same act (one PIN, ADR 0983).
            PasswordResealOutcome? resealed = null;
            PasswordKeyOpener.UserAsked();
            await ActAsync(async () => resealed = await work.Client.MoveIntoAsync(row.Item, from.StoreId, target), select: null);
            if (resealed is { Failed.Count: > 0 } or { SkippedChanged.Count: > 0 })
            {
                await Dialogs.InformAsync(Strings.Get("PwMove"), PasswordNotices.Describe(resealed));
            }
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
    /// Shows who the store is encrypted for, and lets a manager change it (ADR 0983): the view's editor answers the whole
    /// audience, which is saved and re-sealed with ONE PIN in the same act. An administrator appointing a manager only
    /// saves — they hold no key, and the next member's re-seal catches a new member up.
    /// </summary>
    public async Task AudienceAsync(PasswordFolderInfo folder)
    {
        if (tab.Work is not { } work || Dialogs.EditAudience is not { } edit)
        {
            return;
        }

        try
        {
            var store = folder.IsStore ? folder : await work.Client.GetFolderAsync(folder.Links.Href("store")
                ?? throw new InvalidOperationException($"'{folder.Name}' advertised no 'store' rel (ADR 0543)."));
            var audiences = new ContentAudienceClient(work.Core);
            var seen = await audiences.GetAsync(store.Links);
            if (await edit(seen, store.Label) is not { } changed)
            {
                return;
            }

            if (!seen.CanManage)
            {
                await audiences.SaveAsync(seen, changed);
                await Dialogs.InformAsync(Strings.Get("AudTitle"), Strings.Get("AudAppointed"));
            }
            else
            {
                PasswordKeyOpener.UserAsked();
                var (saved, resealed) = await work.Client.ChangeAudienceAsync(store, seen, changed);
                await Dialogs.InformAsync(Strings.Get("AudTitle"), PasswordNotices.DescribeAudience(saved, resealed));
            }
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
    // Every store the caller can decrypt (the stores this tab lists, ADR 0983) with its folders, wherever the caller may
    // create: an item may move into another store, and is sealed to that store's audience on the way. The moving folder
    // and everything beneath it are left out, so nothing is offered a place inside itself.
    private static async Task<IReadOnlyList<PasswordFolderChoice>> FoldersOfAsync(PasswordWork work, PasswordNodeViewModel store, PasswordItemRowViewModel moving)
    {
        var choices = new List<PasswordFolderChoice>();
        var (stores, _) = await work.Client.ListStoresAsync();
        foreach (var listed in stores)
        {
            await WalkAsync(await work.Client.GetFolderAsync(listed.Href), 0);
        }

        return choices;

        async Task WalkAsync(PasswordFolderInfo folder, int depth)
        {
            if (folder.CanCreateFolders)
            {
                choices.Add(new PasswordFolderChoice(folder, depth));
            }

            foreach (var child in folder.Items.Where(i => i.IsFolder && i.Id != moving.Item.Id))
            {
                await WalkAsync(await work.Client.GetFolderAsync(child.Href), depth + 1);
            }
        }
    }
}
