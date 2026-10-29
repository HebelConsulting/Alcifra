using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.ApiClient;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// The Tenant tab's "Offline cache (phone app)" group (#1742, ADR 0923): what the tenant allows the phone's offline cache,
/// edited with the group's pencil like every other settings group. It is its own resource (followed from <c>/me</c>),
/// saved whole with <c>If-Match</c>, and shares the tenant's concurrency token with the other groups, so a save of
/// either re-reads the other: a stale tag would otherwise refuse the next save with 412.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly string[] OfflineCacheUnlockModes = ["UserChooses", "Session", "Document"];
    private static readonly string[] OfflineCacheRefreshModes = ["UserChooses", "KeepUpdated", "RemoveWhenNewer"];

    private ProfileClient.OfflineCachePolicy? _offlineCachePolicy;

    /// <summary>Shown where the installation offers the policy and the caller may edit it (a tenant administrator).</summary>
    [ObservableProperty] private bool _hasTenantOfflineCachePolicy;
    [ObservableProperty] private bool _tenantOcAllowed;
    [ObservableProperty] private bool _tenantOcNotifications;
    [ObservableProperty] private bool _tenantOcOpenedDocuments;
    [ObservableProperty] private bool _tenantOcChosenFolders;
    [ObservableProperty] private int _tenantOcUnlockModeIndex;
    [ObservableProperty] private int _tenantOcRefreshModeIndex;
    [ObservableProperty] private int _tenantOcMaxMinutes = 15;

    public bool IsEditingTenantOfflineCache => TenantEditingGroup == "offline-cache";

    public IReadOnlyList<string> TenantOcUnlockChoices { get; } =
        [Strings.Get("TenantOcUserChooses"), Strings.Get("TenantOcUnlockSession"), Strings.Get("TenantOcUnlockDocument")];

    public IReadOnlyList<string> TenantOcRefreshChoices { get; } =
        [Strings.Get("TenantOcUserChooses"), Strings.Get("TenantOcRefreshKeep"), Strings.Get("TenantOcRefreshRemove")];

    private async Task LoadOfflineCachePolicyAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            ApplyOfflineCachePolicy(await _api.Profile.GetOfflineCachePolicyAsync());
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
        {
            ApplyOfflineCachePolicy(null);   // an installation that answers badly shows no group rather than a wrong one
        }
    }

    private void ApplyOfflineCachePolicy(ProfileClient.OfflineCachePolicy? policy)
    {
        _offlineCachePolicy = policy;
        HasTenantOfflineCachePolicy = policy is { CanEdit: true };
        if (policy is null)
        {
            return;
        }

        TenantOcAllowed = policy.Allowed;
        TenantOcNotifications = policy.Notifications;
        TenantOcOpenedDocuments = policy.OpenedDocuments;
        TenantOcChosenFolders = policy.ChosenFolders;
        TenantOcUnlockModeIndex = Math.Max(0, Array.IndexOf(OfflineCacheUnlockModes, policy.UnlockMode));
        TenantOcRefreshModeIndex = Math.Max(0, Array.IndexOf(OfflineCacheRefreshModes, policy.RefreshMode));
        TenantOcMaxMinutes = policy.MaxUnlockedMinutes;
    }

    private async Task SaveOfflineCachePolicyGroupAsync()
    {
        if (_api is null || _offlineCachePolicy is not { } read)
        {
            return;
        }

        try
        {
            await _api.Profile.SaveOfflineCachePolicyAsync(read with
            {
                Allowed = TenantOcAllowed,
                Notifications = TenantOcNotifications,
                OpenedDocuments = TenantOcOpenedDocuments,
                ChosenFolders = TenantOcChosenFolders,
                UnlockMode = OfflineCacheUnlockModes[Math.Clamp(TenantOcUnlockModeIndex, 0, 2)],
                RefreshMode = OfflineCacheRefreshModes[Math.Clamp(TenantOcRefreshModeIndex, 0, 2)],
                MaxUnlockedMinutes = TenantOcMaxMinutes,
            });
            await LoadTenantSettingsAsync();   // re-reads both: they share one concurrency token
            Status = Strings.Get("StTenantSaved");
        }
        catch (ApiActionException ex)
        {
            ReportError(ex.Message);
        }
    }
}
