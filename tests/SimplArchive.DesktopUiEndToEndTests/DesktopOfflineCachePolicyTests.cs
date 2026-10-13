using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Tenant tab's "Offline cache (phone app)" group (#1742, ADR 0923): edited with the group's pencil like every other
// group, saved whole with If-Match to its own resource. The policy shares the tenant's concurrency token with the other
// groups, so a policy save followed by another group's save must not be refused as stale.
// It changes the shared fixture tenant's policy, so it restores the defaults (ADR 0884's wart, said here).
[Collection(UiCollection.Name)]
public class DesktopOfflineCachePolicyTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopOfflineCachePolicyTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_group_saves_the_policy_and_the_next_settings_save_is_not_stale()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);

        await vm.LoadTenantSettingsAsync();
        Assert.True(vm.HasTenantOfflineCachePolicy);   // the fixture's administrator may edit it
        try
        {
            vm.BeginTenantGroupEditCommand.Execute("offline-cache");
            Assert.True(vm.IsEditingTenantOfflineCache);
            vm.TenantOcUnlockModeIndex = 1;   // Session
            vm.TenantOcChosenFolders = false;
            vm.TenantOcMaxMinutes = 7;
            await vm.SaveTenantGroupEditCommand.ExecuteAsync("offline-cache");

            var saved = await api.Profile.GetOfflineCachePolicyAsync();
            Assert.Equal(("Session", false, 7), (saved!.UnlockMode, saved.ChosenFolders, saved.MaxUnlockedMinutes));
            Assert.Null(vm.TenantEditingGroup);

            // Another group, right after: its tag was re-read with the policy's save, so this is not a 412.
            vm.BeginTenantGroupEditCommand.Execute("security");
            await vm.SaveTenantGroupEditCommand.ExecuteAsync("security");
            Assert.Null(vm.TenantEditingGroup);
            Assert.DoesNotContain("changed", vm.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (await api.Profile.GetOfflineCachePolicyAsync() is { } current)
            {
                await api.Profile.SaveOfflineCachePolicyAsync(current with
                {
                    Allowed = true,
                    Notifications = true,
                    OpenedDocuments = true,
                    ChosenFolders = true,
                    UnlockMode = "UserChooses",
                    RefreshMode = "UserChooses",
                    MaxUnlockedMinutes = 15,
                });
            }
        }
    }
}
