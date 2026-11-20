using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// What the Intray and Check-out ribbons' WebDAV buttons would do if clicked right now, and the tooltip that says so
/// (#461, narrowed by #2394).
/// </summary>
/// <remarks>
/// <para>
/// <b>The Repositories ribbon's WebDAV button no longer uses this</b> (#2394): it opens the Devices window, where each
/// device connects with its own password. The two TAB buttons still mount-and-open their own folder, so they keep the
/// tooltip that says which of the three things will happen.
/// </para>
/// <para>
/// Its own type rather than four more members on <see cref="MainWindowViewModel"/>, which is 6,758 lines and the
/// largest entry on the 1000-line debt list (#466). CLAUDE.md treats adding to an over-limit class as needing
/// the same justification as creating one, and this genuinely is a separate thing: it answers one question —
/// which of three states the button is in — and nothing else in the window depends on it.
/// </para>
/// <para>
/// One button with three behaviours is only honest if the user can tell which they will get, so the TOOLTIP is
/// the feature here, not decoration. An icon that silently changes meaning is worse than three buttons.
/// </para>
/// </remarks>
public sealed partial class WebDavRibbonState : ObservableObject
{
    /// <summary>What clicking will do next: set up credentials, mount, or open what is already mounted.</summary>
    [ObservableProperty]
    private string _tooltip = Strings.Get("MwWebDavSetUpTip");

    /// <summary>
    /// Whether the mount buttons are offered at all (#2311): false on a tenant that refuses plaintext doors, where
    /// WebDAV cannot work — the tooltip then carries the reason. Starts true so nothing flashes disabled before the
    /// first refresh.
    /// </summary>
    [ObservableProperty]
    private bool _canMount = true;

    /// <summary>
    /// Re-reads the state. Cheap in the common case: mounted-ness is answered LOCALLY and first, so a user who
    /// already has the volume costs no request at all; the server is asked only about the devices, which is the
    /// one thing the client cannot know for itself.
    /// </summary>
    public async Task RefreshAsync(Func<Task<Alcifra.ApiClient.DeviceList>>? devices)
    {
        if (OsFileManager.MountedPath() is not null)
        {
            Tooltip = Strings.Get("MwWebDavOpenTip");
            CanMount = true;
            return;
        }

        try
        {
            if (devices is not null)
            {
                var list = await devices();
                CanMount = list.FilesAvailable;
                Tooltip = !list.FilesAvailable
                    ? Strings.Get("DvFilesRefusedStrict")
                    : list.ReachesFiles ? Strings.Get("MwWebDavMountTip") : Strings.Get("MwWebDavSetUpTip");
                return;
            }

            CanMount = true;
            Tooltip = Strings.Get("MwWebDavSetUpTip");
        }
        catch (Exception)
        {
            // Best-effort: an unreachable server leaves the button offering setup, which is harmless — the
            // dialog it opens will report the connection problem itself.
            CanMount = true;
            Tooltip = Strings.Get("MwWebDavSetUpTip");
        }
    }
}
