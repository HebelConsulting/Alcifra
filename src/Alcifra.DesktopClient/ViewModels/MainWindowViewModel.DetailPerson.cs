using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// A person in the details pane, and the one action that acts ON them rather than on the document: <b>Message</b>,
/// which opens the one-to-one chat in the Messages tab (ADR 0974, owner decision 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// <b>"Message" is the Messenger, never the document's comment thread.</b> The two live in different places — the tab
/// and the thread under the pane — and the wording says which is which, here and in every string the action uses.
/// </para>
/// <para>
/// <b>The action is disabled, with the reason in its tooltip, where the Messenger cannot reach the person</b>: the
/// module is not offered on this installation, or the person holds no enrolled key — nothing could be sealed to them,
/// so the Messenger's people search does not offer them. That is a lookup against the module's own answer, never a
/// guess from a name.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    // The Created-by row's person, where the creator was a user (null for a service account). Cleared with the name
    // whenever the pane's subject changes, so no action can execute against the previously selected person (ADR 0559).
    private Guid? _sysCreatedByUserId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCreatedByHint))]
    private bool _canMessageCreatedBy;

    /// <summary>The action's tooltip: what a click does, or why it is disabled.</summary>
    public string MessageCreatedByHint =>
        !MessagesTab.IsOffered ? Strings.Get("DpMessengerUnavailable")
        : CanMessageCreatedBy ? string.Format(CultureInfo.CurrentCulture, Strings.Get("DpMessageHint"), SysCreatedBy)
        : string.Format(CultureInfo.CurrentCulture, Strings.Get("DpMessageNoKey"), SysCreatedBy);

    // The name is the pane's subject marker: cleared (by any of the reset paths, in any partial) means the person is
    // gone with it.
    partial void OnSysCreatedByChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _sysCreatedByUserId = null;
            CanMessageCreatedBy = false;
        }
    }

    /// <summary>Messages the creator in the Messenger, and brings the Messages tab forward when it worked.</summary>
    [RelayCommand]
    private async Task MessageCreatedByAsync()
    {
        if (_sysCreatedByUserId is not { } person || SysCreatedBy.Length == 0)
        {
            return;
        }

        if (await MessagesTab.MessageAsync(person, SysCreatedBy))
        {
            SelectedTab = MessagesTabIndex;
        }
    }

    /// <summary>
    /// Whether the action can act: the Messenger is offered here AND its people search knows the person (an enrolled
    /// key). Asked when the details load, so the disabled state is there before the user clicks (owner decision).
    /// </summary>
    private async Task RefreshCanMessageCreatedByAsync()
    {
        CanMessageCreatedBy = false;
        if (_sysCreatedByUserId is not { } person || SysCreatedBy.Length == 0 || !MessagesTab.IsOffered)
        {
            OnPropertyChanged(nameof(MessageCreatedByHint));
            return;
        }

        CanMessageCreatedBy = await MessagesTab.HasKeyAsync(person, SysCreatedBy);
    }
}
