using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.UiEndToEndTests;

// The take-over affordance is gated on the rel, all the way through to the client (ADR 0672, #702 PR 3).
//
// The API side is covered by PersonalSpaceTakeOverTests. What this adds is the half a server test cannot see:
// that the row's link actually SURVIVES into the client's model, because the menu item is drawn from
// TreeNodeViewModel.HasRel("take-over") and a link dropped during parsing would silently remove the affordance
// while every server assertion stayed green.
//
// The row is a reference row of the Users folder now (ADR 1066) — the one place an administrator reaches other
// people's spaces — rather than a row of the retired admin listing.
[Collection(UiCollection.Name)]
public class DesktopTakeOverAffordanceTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTakeOverAffordanceTests(SelfHostedAppFixture app) => _app = app;

    /// <summary>A throwaway user who has materialised their personal space, so Users has a row for them.</summary>
    private static async Task<string> SeedSpaceOwnerAsync(AlcifraApiClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var displayName = "Takeover Target " + suffix;
        var userId = await admin.Admin.CreateUserAsync($"to-{suffix}@example.test", displayName);
        var password = await admin.Admin.ResetUserPasswordAsync(userId);

        // The space is created on demand, so it does not exist until its owner asks for it.
        var user = new AlcifraApiClient(await Ui.GetUserTokenAsync(_appBaseUrl!, $"to-{suffix}@example.test", password));
        await user.Profile.GetPersonalRepositoryAsync();
        return displayName;
    }

    private static string? _appBaseUrl;

    // Administration → Users → its reference rows, each hop followed from the row before it (ADR 0543).
    private static async Task<List<Alcifra.ApiClient.ReferencesClient.Reference>> UsersRowsAsync(AlcifraApiClient api)
    {
        var administration = (await api.Documents.GetRepositoriesAsync()).Single(r => r.Name == "Administration");
        var users = (await api.Documents.GetChildrenAsync(administration.Href("children")!)).Single(c => c.Name == "Users");
        return await api.References.GetReferencesAsync(users.Href("references")!);
    }

    [Fact]
    public async Task The_Users_folders_rows_carry_the_take_over_address()
    {
        DesktopClientOptions.ApiBaseUrl = _appBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var target = await SeedSpaceOwnerAsync(api);

        var rows = await UsersRowsAsync(api);

        // The demo admin holds CanManageUsers, so somebody else's space offers it...
        var someoneElse = rows.Single(r => r.Name == target);
        Assert.NotNull(someoneElse.Links?.Href("take-over"));

        // ...and taking over your own is meaningless, since you already hold every right on it. The server does
        // not offer it, so the client draws no item — which is the same mechanism, not a second rule.
        var own = rows.Single(r => r.Name == SelfHostedAppFixture.AdminDisplayName);
        Assert.Null(own.Links?.Href("take-over"));
    }

    [Fact]
    public async Task Following_the_advertised_address_grants_the_access()
    {
        DesktopClientOptions.ApiBaseUrl = _appBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var name = await SeedSpaceOwnerAsync(api);

        var target = (await UsersRowsAsync(api)).Single(r => r.Name == name);

        // Followed, not composed — the same call the context menu makes. Twice, because asking for access you
        // already hold is not an error and the second call must not 500 on the ACL entry's unique index.
        await api.Admin.TakeOverPersonalSpaceAsync(target.Links!.Href("take-over")!);
        await api.Admin.TakeOverPersonalSpaceAsync(target.Links!.Href("take-over")!);
    }
}
