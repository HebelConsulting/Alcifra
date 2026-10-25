using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Passwords tab (ADR 0968; parity with the phone's Password Store, Mobile ADR 0037, over core ADR 0964): the stores
/// in the tree — My Passwords first, created on first use, then the shared ones — a folder's folders and entries in the
/// list, and an opened entry in the PREVIEW pane, opened with the hardware key and held in memory only. Shown only where
/// the installation advertises <c>passwordStores</c>. Creating, editing, renaming, moving, deleting, re-sealing and
/// importing are this tab's (<see cref="PasswordStoreActions"/> carries the ones that are not the selection's).
/// </summary>
public sealed partial class PasswordsTabViewModel : ObservableObject
{
    private readonly IShellContext _shell;
    private readonly Func<SimplArchiveApiClient?> _api;
    private readonly SecretFieldServices _services;
    private SimplArchiveApiClient? _boundTo;
    private PasswordWork? _work;
    private OpenedPasswordEntry? _opened;
    private int _openStamp;

    /// <param name="api">The signed-in client, read when needed: the tab is built before sign-in.</param>
    /// <param name="services">The window's clipboards, screen protection and clock for a secret field (ADR 0730: the
    /// shell's, so a constructor argument).</param>
    public PasswordsTabViewModel(IShellContext shell, Func<SimplArchiveApiClient?> api, SecretFieldServices services)
    {
        (_shell, _api, _services) = (shell, api, services);
        Preview = new PreviewViewModel(shell);
        Preview.Reset(Strings.Get("PwSelectEntry"));
        Actions = new PasswordStoreActions(this);
    }

    /// <summary>The tab's own preview (ADR "Desktop recycle bin parity"): never entangled with the Repositories one.</summary>
    public PreviewViewModel Preview { get; }

    public SecretScreens Screens => _services.Screens;

    public PasswordDialogs Dialogs { get; } = new();

    public PasswordStoreActions Actions { get; }

    public ObservableCollection<PasswordNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<PasswordItemRowViewModel> Items { get; } = [];

    public ObservableCollection<PasswordNoticeViewModel> Notices { get; } = [];

    /// <summary>The installation offers the password store to this user: what shows the tab.</summary>
    [ObservableProperty] private bool _isOffered;

    [ObservableProperty] private PasswordNodeViewModel? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEntryName))]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(MoveCommand), nameof(DeleteCommand))]
    private PasswordItemRowViewModel? _selectedItem;

    /// <summary>The form on show (create or edit): it takes the preview's place, its Save and Cancel in the Edit slot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanOpenWithKey))]
    private PasswordEntryEditorViewModel? _editor;

    /// <summary>The opened entry may be edited (its row's flag), and nothing is being edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanOpenWithKey))]
    private bool _entryOpen;

    [ObservableProperty] private string _emptyMessage = string.Empty;

    public bool IsEditing => Editor is not null;

    public bool HasSelectedEntry => SelectedItem is { IsFolder: false };

    public string SelectedEntryName => Editor?.Title ?? SelectedItem?.Name ?? string.Empty;

    /// <summary>Edit sits beside the entry's name; it gives way to Save and Cancel while editing (ADR 0550).</summary>
    public bool CanEdit => !IsEditing && EntryOpen && _opened?.Entry.CanEdit == true;

    /// <summary>An entry is selected but not open (the PIN prompt was cancelled, or the key left): open it again.</summary>
    public bool CanOpenWithKey => !IsEditing && !EntryOpen && HasSelectedEntry;

    /// <summary>
    /// The folder on show offers new entries (its <c>password-entries</c> rel): the server sends it only where the user may
    /// create AND what they save can be sealed to a key they hold (ADR 0982). Import creates entries, so it follows too.
    /// </summary>
    public bool CanCreateEntry => SelectedNode?.Folder?.CanCreateEntries == true;

    /// <summary>The folder on show offers new folders (its <c>password-folders</c> rel; folders are not sealed).</summary>
    public bool CanCreateFolder => SelectedNode?.Folder?.CanCreateFolders == true;

    /// <summary>
    /// New entry's tooltip: what it does, or — while the server withholds it — why (ADR 0982), so the reason sits on the
    /// disabled button the user reached for, as well as in the folder's notice.
    /// </summary>
    public string NewEntryHint => SelectedNode?.Folder is { } folder && PasswordNotices.WhyNoNewEntry(folder) is { } why
        ? why
        : Strings.Get("PwNewEntry");

    public bool CanReseal => SelectedNode?.Folder?.CanReseal == true;

    /// <summary>The signed-in client, for a dialog the view opens on the tab's behalf (the spare key's enrolment).</summary>
    public SimplArchiveApiClient? CurrentApi => _api();

    internal PasswordWork? Work => ReferenceEquals(_boundTo, _api()) ? _work : null;

    internal SecretFieldServices Services => _services;

    internal IShellContext Shell => _shell;

    /// <summary>
    /// Asks the installation whether it offers the password store here; the tab shows only if it does. Run at every
    /// sign-in, so a session never keeps the client of an ended one.
    /// </summary>
    public async Task DiscoverAsync()
    {
        var api = _api();
        Forget();
        if (api is null)
        {
            IsOffered = false;
            return;
        }

        var offered = false;
        await PasswordStoreList.WhenOfferedAsync(api.Core, () => offered = true);
        if (!ReferenceEquals(api, _api()))
        {
            return;
        }

        (_boundTo, _work, IsOffered) = (api, offered ? new PasswordWork(api.Core, PasswordKeyOpener.Instance) : null, offered);
    }

    /// <summary>Signed out: what was opened is forgotten and the tab goes.</summary>
    public void Reset()
    {
        Forget();
        IsOffered = false;
    }

    private void Forget()
    {
        CloseEntry(Strings.Get("PwSelectEntry"));
        Editor = null;
        (_boundTo, _work) = (null, null);
        SelectedItem = null;
        SelectedNode = null;
        Items.Clear();
        Notices.Clear();
        Nodes.Clear();
    }

    /// <summary>Refresh: the stores, then the folder on show, kept selected by id.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_boundTo is not null && Work is null)
        {
            await DiscoverAsync();
        }

        if (Work is not { } work)
        {
            return;
        }

        var keep = SelectedNode?.Folder?.Id;
        IReadOnlyList<PasswordStoreRow> rows;
        try
        {
            rows = await work.Stores.RowsAsync();
        }
        catch (Exception e) when (IsReported(e))
        {
            Failed(e);
            return;
        }

        Nodes.Clear();
        foreach (var row in rows)
        {
            Nodes.Add(new PasswordNodeViewModel(row.Name, isStore: true, row.IsPersonal, row.Href, null, null));
        }

        EmptyMessage = Nodes.Count == 0 ? Strings.Get("PwNoStores") : string.Empty;
        SelectedNode = null;
        SelectedNode = keep is { } id ? await RevealAsync(id) ?? Nodes.FirstOrDefault() : Nodes.FirstOrDefault();
    }

    // A folder found again after a refresh: its store read, then the path down to it as far as it is still listed.
    private async Task<PasswordNodeViewModel?> RevealAsync(Guid folderId)
    {
        foreach (var store in Nodes.Where(n => n.Href is not null))
        {
            if (await ReadAsync(store) is null)
            {
                continue;
            }

            if (store.Find(folderId) is { } found)
            {
                for (var up = found.Parent; up is not null; up = up.Parent)
                {
                    up.IsExpanded = true;
                }

                return found;
            }
        }

        return null;
    }

    partial void OnSelectedNodeChanged(PasswordNodeViewModel? value)
    {
        SelectedItem = null;
        Items.Clear();
        Notices.Clear();
        OnPropertyChanged(nameof(CanCreateEntry));
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(NewEntryHint));
        OnPropertyChanged(nameof(CanReseal));
        if (value is not null)
        {
            Safe.Fire(() => ShowFolderAsync(value));
        }
    }

    /// <summary>Reads the folder on show again (after a change made here) and lists it.</summary>
    internal async Task ReloadFolderAsync(Guid? select = null)
    {
        if (SelectedNode is { } node)
        {
            await ShowFolderAsync(node, select);
        }
    }

    private async Task ShowFolderAsync(PasswordNodeViewModel node, Guid? select = null)
    {
        if (await ReadAsync(node) is not { } folder || !ReferenceEquals(node, SelectedNode))
        {
            return;
        }

        node.IsExpanded = true;
        Items.Clear();
        foreach (var item in folder.Items.OrderBy(i => !i.IsFolder).ThenBy(i => i.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true)))
        {
            Items.Add(new PasswordItemRowViewModel(item));
        }

        Notices.Clear();
        foreach (var notice in PasswordNotices.For(folder))
        {
            Notices.Add(new PasswordNoticeViewModel(notice));
        }

        OnPropertyChanged(nameof(CanCreateEntry));
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(NewEntryHint));
        OnPropertyChanged(nameof(CanReseal));
        if (select is { } id)
        {
            SelectedItem = Items.FirstOrDefault(i => i.Item.Id == id);
        }
    }

    // A store or folder read; My Passwords is created here the first time it is opened.
    internal async Task<PasswordFolderInfo?> ReadAsync(PasswordNodeViewModel node)
    {
        if (Work is not { } work)
        {
            return null;
        }

        try
        {
            var folder = node.Href is { } href ? await work.Client.GetFolderAsync(href) : await work.Client.EnsureMyPasswordsAsync();
            node.Read(folder);
            return folder;
        }
        catch (Exception e) when (IsReported(e))
        {
            Failed(e);
            return null;
        }
    }

    // The selection is the tab's (ADR 0661): a new row closes what the previous one opened before anything else happens.
    partial void OnSelectedItemChanged(PasswordItemRowViewModel? value)
    {
        if (Editor is { IsCreating: false })
        {
            Editor = null;
        }

        CloseEntry(value is { IsFolder: false } ? Strings.Get("PwOpening") : Strings.Get("PwSelectEntry"));
        if (value is { IsFolder: false } row)
        {
            Safe.Fire(() => OpenAsync(row));
        }
    }

    /// <summary>A folder row double-clicked: it opens in the tree.</summary>
    public void Enter(PasswordItemRowViewModel row)
    {
        if (row.IsFolder && SelectedNode?.Children.FirstOrDefault(c => c.Row?.Id == row.Item.Id) is { } child)
        {
            SelectedNode = child;
        }
    }

    /// <summary>Opens the selected entry with the key again, after a cancelled prompt or the key leaving.</summary>
    [RelayCommand]
    private Task OpenWithKeyAsync() => SelectedItem is { IsFolder: false } row ? OpenAsync(row) : Task.CompletedTask;

    /// <summary>
    /// Opens the row's entry with the hardware key and shows it in the preview, in memory. A cancelled PIN prompt shows
    /// the entry as sealed with "Open with key" beside it; nothing is ever shown of a previous entry meanwhile.
    /// </summary>
    internal async Task OpenAsync(PasswordItemRowViewModel row)
    {
        if (Work is not { } work)
        {
            return;
        }

        var stamp = ++_openStamp;
        PasswordKeyOpener.UserAsked();
        try
        {
            var opened = await work.OpenAsync(row.Item);
            if (stamp != _openStamp || !ReferenceEquals(row, SelectedItem))
            {
                return;   // another row was chosen while the key was being asked: this one is not shown
            }

            _opened = opened;
            Preview.ShowPasswordEntry(new PasswordEntryPreviewViewModel(opened.Entry.Name, opened.Content, opened.Entry, _services, isReadOnly: false));
            EntryOpen = true;
        }
        catch (Exception e) when (IsReported(e) && stamp == _openStamp)
        {
            CloseEntry(PasswordWork.Describe(e) is { } said ? said : Strings.Get("PwOpenToRead"));
        }
    }

    private void CloseEntry(string placeholder)
    {
        _openStamp++;
        _opened = null;
        EntryOpen = false;
        Preview.Reset(placeholder);
    }

    /// <summary>Edit, beside the entry's name: the form filled from what the key opened, its ETag what Save sends.</summary>
    [RelayCommand]
    private void Edit()
    {
        if (CanEdit && Work is { } work && _opened is { } opened)
        {
            Editor = PasswordEntryEditorViewModel.Edit(work.Client, opened, Screens, (saved, failure) => EditDoneAsync(opened.Entry.Id, saved, failure));
        }
    }

    /// <summary>New entry in the folder on show: sealed on this machine when saved.</summary>
    [RelayCommand]
    private void NewEntry()
    {
        if (CanCreateEntry && Work is { } work && SelectedNode?.Folder is { } folder)
        {
            SelectedItem = null;
            Editor = PasswordEntryEditorViewModel.Create(work.Client, folder, Screens, (saved, failure) => EditDoneAsync(null, saved, failure));
        }
    }

    // The form closed: saved (the entry shown again, read fresh), refused because it changed meanwhile (said, and read
    // again with the key), or cancelled (the entry as it was).
    private async Task EditDoneAsync(Guid? editing, PasswordEntryInfo? saved, Exception? failure)
    {
        Editor = null;
        if (saved is null && failure is null)
        {
            return;   // cancelled: the entry on show is as it was
        }

        if (failure is not null)
        {
            _shell.Report(Strings.Get("PwEntryChanged"));
        }

        // Selected afresh, so it is read again and opened with the key: the version on show is the one just filed, or,
        // after a 412, the one somebody else filed.
        SelectedItem = null;
        await ReloadFolderAsync(select: saved?.Id ?? editing);
    }

    /// <summary>Esc: the form goes first.</summary>
    public bool Escape()
    {
        if (Editor is { } editor)
        {
            Safe.Fire(editor.CancelAsync);
            return true;
        }

        return false;
    }

    /// <summary>Cmd/Ctrl+S: the form saves.</summary>
    public bool Save()
    {
        if (Editor is { } editor && editor.SaveCommand.CanExecute(null))
        {
            Safe.Fire(editor.SaveAsync);
            return true;
        }

        return false;
    }

    private bool CanActOnRow() => SelectedItem is not null && !IsEditing;

    [RelayCommand(CanExecute = nameof(CanActOnRow))]
    private Task RenameAsync() => SelectedItem is { } row ? Actions.RenameAsync(row) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanActOnRow))]
    private Task MoveAsync() => SelectedItem is { } row ? Actions.MoveAsync(row) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanActOnRow))]
    private Task DeleteAsync() => SelectedItem is { } row ? Actions.DeleteAsync(row) : Task.CompletedTask;

    [RelayCommand]
    private Task NewFolderAsync() => SelectedNode?.Folder is { } folder ? Actions.NewFolderAsync(folder) : Task.CompletedTask;

    [RelayCommand]
    private Task ResealAsync() => SelectedNode?.Folder is { } folder ? Actions.ResealAsync(folder) : Task.CompletedTask;

    [RelayCommand]
    private Task ImportAsync() => SelectedNode?.Folder is { } folder ? Actions.ImportAsync(folder) : Task.CompletedTask;

    /// <summary>A notice's own button: re-seal, enrol a spare key, or enrol a first key (then read the folder again, so
    /// New entry appears once the server can seal to the new key).</summary>
    [RelayCommand]
    private Task NoticeActionAsync(PasswordNoticeViewModel? notice) => notice?.Notice.Action switch
    {
        PasswordNoticeAction.Reseal => ResealAsync(),
        PasswordNoticeAction.EnrolSpareKey => Dialogs.EnrolKey?.Invoke() ?? Task.CompletedTask,
        PasswordNoticeAction.EnrolKey => EnrolThenReloadAsync(),
        _ => Task.CompletedTask,
    };

    private async Task EnrolThenReloadAsync()
    {
        if (Dialogs.EnrolKey is { } enrol)
        {
            await enrol();
            await ReloadFolderAsync();
        }
    }

    /// <summary>
    /// The key left the machine: what it opened is closed — the entry on show and an edit of it — as the phone's
    /// sealed-content guard does. Nothing comes back by itself; "Open with key" opens it again.
    /// </summary>
    public void KeyLeft(string? placeholder = null)
    {
        if (Editor is { IsCreating: false } editor)
        {
            editor.HideSecrets();
            Editor = null;
        }

        CloseEntry(placeholder ?? Strings.Get("PwOpenToRead"));
    }

    /// <summary>The window left the front: every secret held open is hidden.</summary>
    public void WindowDeactivated()
    {
        Preview.HideSecrets();
        Editor?.HideSecrets();
    }

    partial void OnEditorChanged(PasswordEntryEditorViewModel? oldValue, PasswordEntryEditorViewModel? newValue)
    {
        oldValue?.HideSecrets();
        OnPropertyChanged(nameof(SelectedEntryName));
        RenameCommand.NotifyCanExecuteChanged();
        MoveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    // A failure the user is told of on the status line; the key prompt's Cancel is said by nothing.
    internal void Failed(Exception e)
    {
        DesktopLog.Debug("Passwords: an act did not complete: {Reason}", e.Message);
        if (PasswordWork.Describe(e) is { } said)
        {
            _shell.Report(said);
        }
    }

    internal static bool IsReported(Exception e) => e is HttpRequestException or InvalidOperationException or EnvelopeNotOpenedException
        or PasswordEntryUnreadableException or PasswordEntryChangedException or PasswordStoreHasNoRecipientsException or PasswordEntryCreationWithheldException or DocumentNameTakenException
        or OperationCanceledException or SessionEndedException or System.Text.Json.JsonException or ApiActionException;
}
