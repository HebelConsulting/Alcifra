using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.UiEndToEndTests;

// The administrator's way into every personal space (ADR 1066), through the real api client: the Administration folder
// is a repository root like any other, Users beneath it references each user's space, and the admin browses into one
// through the addresses the reference row advertises — even a throwaway user's freshly-created space, which the core
// referenced the moment it was provisioned.
[Collection(UiCollection.Name)]
public class DesktopAdminUsersTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdminUsersTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Admin_reaches_and_browses_a_users_personal_space_through_the_Users_folder()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A throwaway user with a personal repository, made after the Users folder existed — so the row below is the
        // one the provisioner placed, not one a start reconciled.
        var userId = await admin.Admin.CreateUserAsync($"au-{suffix}@example.test", "AdminView User " + suffix);
        var password = await admin.Admin.ResetUserPasswordAsync(userId);
        var user = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, $"au-{suffix}@example.test", password));
        var userRepo = await user.Profile.GetPersonalRepositoryAsync();
        Assert.NotNull(userRepo);

        // Administration → Users → the spaces, each hop followed from the row before it (ADR 0543).
        var administration = (await admin.Documents.GetRepositoriesAsync()).Single(r => r.Name == "Administration");
        var users = (await admin.Documents.GetChildrenAsync(administration.Href("children")!)).Single(c => c.Name == "Users");
        var rows = await admin.References.GetReferencesAsync(users.Href("references")!);
        var row = Assert.Single(rows, r => r.TargetId == userRepo!.Id);

        // The admin can browse into it (empty, but reachable) — through the address the row advertised.
        var children = await admin.Documents.GetChildrenAsync(row.Links!.Href("children")!);
        Assert.NotNull(children);
    }
}
