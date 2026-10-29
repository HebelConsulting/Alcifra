using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.DesktopClient.Services;
using SimplArchive.Messenger.Client;
using SimplArchive.Messenger.Client.Api;
using SimplArchive.Messenger.Client.Display;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>What the people picker is for.</summary>
public enum MessengerPeopleMode
{
    Chat,
    Group,
    AddMember,
}

/// <summary>A person the picker offers; chosen by a tick in a new group.</summary>
public sealed partial class MessengerPersonViewModel(Person person) : ObservableObject
{
    public Person Person { get; } = person;

    public string Name => Person.DisplayName;

    public string Email => Person.Email;

    [ObservableProperty] private bool _isChosen;
}

/// <summary>
/// Choosing who to talk to (ADR 0967, the phone's people screen): one person for a chat, a name and several people for a
/// group, or one more member for a group. Only people with an enrolled key are offered — nothing can be sealed to anyone
/// else — and the pane says so. Shown in the Messages tab's right pane, the conversations staying beside it.
/// </summary>
public sealed partial class MessengerPeopleViewModel : ObservableObject
{
    private readonly ChatSession _session;
    private readonly ConversationList? _list;
    private readonly Conversation? _group;
    private readonly MessengerDialogs _dialogs;
    private readonly Action<Conversation?> _done;
    private readonly Action<Exception> _failed;
    private int _search;   // the newest search: an older answer arriving later is dropped

    /// <param name="done">The chat or group made (shown), or null: cancelled, or a member added (back to where it was).</param>
    public MessengerPeopleViewModel(
        ChatSession session, MessengerPeopleMode mode, ConversationList? list, Conversation? group, MessengerDialogs dialogs,
        Action<Conversation?> done, Action<Exception> failed)
    {
        (_session, Mode, _list, _group, _dialogs, _done, _failed) = (session, mode, list, group, dialogs, done, failed);
    }

    public MessengerPeopleMode Mode { get; }

    public string Title => ViewText.Get(Mode switch { MessengerPeopleMode.Group => "NewGroup", MessengerPeopleMode.AddMember => "AddMember", _ => "NewChat" });

    public bool IsGroup => Mode == MessengerPeopleMode.Group;

    public string Hint => ViewText.Get("PeopleHint");

    public ObservableCollection<MessengerPersonViewModel> People { get; } = [];

    [ObservableProperty] private string _term = string.Empty;

    [ObservableProperty] private string _groupName = string.Empty;

    // Searched as typed, from two letters on (or emptied: everyone again), as on the phone.
    partial void OnTermChanged(string value)
    {
        if (value.Length is 0 or >= 2)
        {
            Safe.Fire(() => SearchAsync(value));
        }
    }

    /// <summary>Everyone with a key, before anything is typed.</summary>
    public Task LoadAsync() => SearchAsync(string.Empty);

    internal async Task SearchAsync(string term)
    {
        var mine = ++_search;
        try
        {
            // Neither the caller nor, when adding, anyone already in the group.
            var already = _group?.Members.Select(m => m.UserId).ToHashSet() ?? [];
            var found = (await _session.Api.PeopleAsync(term)).Items;
            if (mine != _search)
            {
                return;
            }

            People.Clear();
            foreach (var person in found.Where(p => p.Id != _session.Me && !already.Contains(p.Id)))
            {
                People.Add(new MessengerPersonViewModel(person));
            }
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _failed(e);
        }
    }

    /// <summary>A click on a person: a chat starts, or — confirmed — they join the group with a new key for everyone.</summary>
    [RelayCommand]
    private async Task ChooseAsync(MessengerPersonViewModel? person)
    {
        if (person is null)
        {
            return;
        }

        if (IsGroup)
        {
            person.IsChosen = !person.IsChosen;
            return;
        }

        try
        {
            if (Mode == MessengerPeopleMode.AddMember)
            {
                if (_group is null || !await _dialogs.ConfirmAsync(ViewText.Get("AddMember"), ViewText.Get("AddConfirm", person.Name), ViewText.Get("AddMember")))
                {
                    return;
                }

                // A new key for everyone, the newcomer included: they read from now on, nothing from before.
                await _session.AddAsync(_group, person.Person);
                _done(null);
                return;
            }

            if (_list is not null)
            {
                _done(await _session.StartDirectAsync(_list, person.Person));
            }
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _failed(e);
        }
    }

    /// <summary>Create: the named group of the ticked people, with a new key for everyone.</summary>
    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var name = GroupName.Trim();
        var chosen = People.Where(p => p.IsChosen).Select(p => p.Person).ToList();
        if (_list is null || name.Length == 0 || chosen.Count == 0)
        {
            return;
        }

        try
        {
            _done(await _session.CreateGroupAsync(_list, name, chosen));
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _failed(e);
        }
    }

    /// <summary>✕ or Esc: nothing is made, back to where it was.</summary>
    [RelayCommand]
    public void Cancel() => _done(null);
}

/// <summary>A member as the members pane draws them, and what the caller may do with them (from the server's links).</summary>
public sealed class MessengerMemberViewModel(MemberRow row)
{
    public MemberRow Row { get; } = row;

    public string Name => Row.Name;

    public bool IsAdmin => Row.IsAdmin;

    public string AdminLabel => ViewText.Get("Admin");
}

/// <summary>
/// A group's members (ADR 0967, core #1810): administrators first, each offering what the caller may do — make or revoke
/// administrator, remove, or for oneself leave — and, for an administrator, adding someone. Removing and adding make a
/// new key on THIS machine, for those who remain or with the newcomer; the last administrator is the server's rule, and
/// its refusal says so.
/// </summary>
public sealed partial class MessengerMembersViewModel : ObservableObject
{
    private readonly ChatSession _session;
    private readonly MessengerDialogs _dialogs;
    private readonly Action<Conversation> _addMember;
    private readonly Action _left;
    private readonly Action _close;
    private readonly Action<Exception> _failed;

    public MessengerMembersViewModel(
        ChatSession session, Conversation group, MessengerDialogs dialogs, Action<Conversation> addMember, Action left, Action close, Action<Exception> failed)
    {
        (_session, Group, _dialogs, _addMember, _left, _close, _failed) = (session, group, dialogs, addMember, left, close, failed);
    }

    public Conversation Group { get; private set; }

    public string Title => $"{ViewText.Get("Members")} · {ThreadRows.Title(Group)}";

    public ObservableCollection<MessengerMemberViewModel> Members { get; } = [];

    [ObservableProperty] private bool _canAdd;

    public async Task LoadAsync()
    {
        try
        {
            Group = await _session.Api.RefreshAsync(Group);
            Members.Clear();
            foreach (var row in MemberRows.Build(Group, _session.Me))
            {
                Members.Add(new MessengerMemberViewModel(row));
            }

            CanAdd = Group.Offers("add-member");
            OnPropertyChanged(nameof(Title));
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _failed(e);
        }
    }

    [RelayCommand]
    private void AddMember() => _addMember(Group);

    [RelayCommand]
    private Task MakeAdminAsync(MessengerMemberViewModel? member) =>
        member is { Row.CanMakeAdmin: true } ? ActAsync(() => _session.Api.MakeAdminAsync(member.Row.Member)) : Task.CompletedTask;

    [RelayCommand]
    private Task RevokeAdminAsync(MessengerMemberViewModel? member) =>
        member is { Row.CanRevokeAdmin: true } ? ActAsync(() => _session.Api.RevokeAdminAsync(member.Row.Member)) : Task.CompletedTask;

    [RelayCommand]
    private async Task RemoveAsync(MessengerMemberViewModel? member)
    {
        if (member is { Row.CanRemove: true }
            && await _dialogs.ConfirmAsync(ViewText.Get("Remove"), ViewText.Get("RemoveConfirm", member.Name), ViewText.Get("Remove")))
        {
            // The next key is made here, on this administrator's machine, for those who remain.
            await ActAsync(() => _session.RemoveAsync(Group, member.Row.Member));
        }
    }

    [RelayCommand]
    private async Task LeaveAsync(MessengerMemberViewModel? member)
    {
        if (member is not { Row.CanLeave: true } || !await _dialogs.ConfirmAsync(ViewText.Get("Leave"), ViewText.Get("LeaveConfirm"), ViewText.Get("Leave")))
        {
            return;
        }

        try
        {
            await _session.LeaveAsync(Group);
            _left();   // out of the group: its thread can no longer be opened
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _failed(e);
        }
    }

    [RelayCommand]
    public void Close() => _close();

    private async Task ActAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception e) when (MessengerRefusal.IsReported(e))
        {
            _failed(e);
        }

        await LoadAsync();
    }
}
