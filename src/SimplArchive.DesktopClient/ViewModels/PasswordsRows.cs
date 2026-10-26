using SimplArchive.ApiClient;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// A store or a folder in the Passwords tab's tree (ADR 0968): My Passwords first (created on first use), then the shared
/// stores, each with its folders beneath. What it holds once read is <see cref="Folder"/>; its own row in its parent's
/// listing (flags, ETag, rels) is <see cref="Row"/>, null for a store.
/// </summary>
public sealed partial class PasswordNodeViewModel : ObservableObject
{
    public PasswordNodeViewModel(string name, bool isStore, bool isPersonal, string? href, PasswordItemInfo? row, PasswordNodeViewModel? parent)
    {
        (Name, IsStore, IsPersonal, Href, Row, Parent) = (name, isStore, isPersonal, href, row, parent);
    }

    public string Name { get; }

    public bool IsStore { get; }

    public bool IsPersonal { get; }

    /// <summary>The folder's contents; null for My Passwords before it exists (it is created when first opened).</summary>
    public string? Href { get; private set; }

    /// <summary>The row its parent listed it with (a folder's rename, move and delete); null for a store.</summary>
    public PasswordItemInfo? Row { get; }

    public PasswordNodeViewModel? Parent { get; }

    public ObservableCollection<PasswordNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    /// <summary>What it held when last read.</summary>
    public PasswordFolderInfo? Folder { get; private set; }

    public string Icon => IsStore ? IsPersonal ? "mdi-account-key-outline" : "mdi-safe-square-outline" : "mdi-folder-key-outline";

    /// <summary>The store this node belongs to: itself, or its topmost ancestor.</summary>
    public PasswordNodeViewModel Store => Parent?.Store ?? this;

    /// <summary>Takes in a read of the folder: its href (My Passwords now exists) and its sub-folders, kept where they were.</summary>
    internal void Read(PasswordFolderInfo folder)
    {
        Folder = folder;
        Href ??= folder.Links.Href("self");
        var folders = folder.Items.Where(i => i.IsFolder).ToList();
        var kept = Children.Where(c => folders.Any(f => f.Id == c.Row?.Id)).ToDictionary(c => c.Row!.Id);
        Children.Clear();
        foreach (var item in folders)
        {
            Children.Add(kept.TryGetValue(item.Id, out var same) && same.Row!.ETag == item.ETag && same.Name == item.Name
                ? same
                : new PasswordNodeViewModel(item.Name, false, IsPersonal, item.Href, item, this));
        }
    }

    /// <summary>The node for a folder id among this node and its descendants, or null.</summary>
    internal PasswordNodeViewModel? Find(Guid folderId) =>
        Folder?.Id == folderId || Row?.Id == folderId ? this : Children.Select(c => c.Find(folderId)).FirstOrDefault(n => n is not null);
}

/// <summary>A row of the Passwords tab's list: a folder or an entry of the folder on show.</summary>
public sealed class PasswordItemRowViewModel(PasswordItemInfo item)
{
    public PasswordItemInfo Item { get; } = item;

    public string Name => Item.Name;

    public bool IsFolder => Item.IsFolder;

    public string Icon => IsFolder ? "mdi-folder-key-outline" : "mdi-key-variant";
}

/// <summary>A notice above the list (ADR 0964): re-seal, urgent re-seal, the spare key, nothing to seal to.</summary>
public sealed class PasswordNoticeViewModel(PasswordNotice notice)
{
    public PasswordNotice Notice { get; } = notice;

    public string Text => Notice.Text;

    public bool Urgent => Notice.Urgent;

    public bool HasAction => Notice.Action != PasswordNoticeAction.None;

    public string ActionLabel => Notice.Action switch
    {
        PasswordNoticeAction.Reseal => Strings.Get("PwReseal"),
        PasswordNoticeAction.EnrolSpareKey => Strings.Get("PwEnrolSpareKey"),
        PasswordNoticeAction.EnrolKey => Strings.Get("PwEnrolKey"),
        _ => string.Empty,
    };

    public string Icon => Urgent ? "mdi-alert-outline" : "mdi-information-outline";
}

/// <summary>A folder a password item can move to, indented under its parent (ADR 0968): in any store the caller can
/// decrypt, its store at depth 0 (ADR 0983).</summary>
public sealed record PasswordFolderChoice(PasswordFolderInfo Folder, int Depth)
{
    public string Label => $"{new string(' ', Depth * 4)}{Folder.Label}";
}

/// <summary>An export picked for import: its name, its text, and where it is, so it can be deleted afterwards.</summary>
public sealed record PickedPasswordExport(string Name, string Text, string? Path);

/// <summary>
/// The dialogs the Passwords tab asks the view for (ADR 0730: the view's, so a settable callback each). Each answers
/// "cancelled" when no view has bound it, which disables the act loudly rather than silently doing something else.
/// </summary>
public sealed class PasswordDialogs
{
    /// <summary>A yes/no question: title, message and the confirming button's label.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>A message to read and close.</summary>
    public Func<string, string, Task>? Inform { get; set; }

    /// <summary>A name: the dialog's title, its label and the name it starts with; null when cancelled.</summary>
    public Func<string, string, string, Task<string?>>? AskName { get; set; }

    /// <summary>A target folder among the given choices; null when cancelled.</summary>
    public Func<IReadOnlyList<PasswordFolderChoice>, Task<PasswordFolderInfo?>>? PickFolder { get; set; }

    /// <summary>The export to import; null when cancelled.</summary>
    public Func<Task<PickedPasswordExport?>>? PickExport { get; set; }

    /// <summary>One clashing name's answer: skip, a new version, keep both; null cancels the whole import.</summary>
    public Func<PasswordImportConflict, Task<PasswordImportResolution?>>? AskConflict { get; set; }

    /// <summary>Deletes the picked export itself; false where it could not be.</summary>
    public Func<string, Task<bool>>? DeleteFile { get; set; }

    /// <summary>Enrols a key, a first or a spare one (the self-enrolment dialog).</summary>
    public Func<Task>? EnrolKey { get; set; }

    /// <summary>The audience editor (ADR 0983): the audience and the store's label in, the whole audience to save out;
    /// null when cancelled or when only read.</summary>
    public Func<ContentAudienceInfo, string, Task<IReadOnlyList<AudienceChange>?>>? EditAudience { get; set; }

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm) => Confirm is not null && await Confirm(title, message, confirm);

    internal Task InformAsync(string title, string message) => Inform?.Invoke(title, message) ?? Task.CompletedTask;
}
