using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Alcifra.UiEndToEndTests;

// A tenant of a test's own with the TestModule active in it: every other test here shares the demo tenant, and its New
// menus and module list must not change under them. Shared by the tests that drive a module's field editors.
internal static class TestModuleTenant
{
    // A tenant for one test alone, provisioned as the platform administrator: its administrator's token and repository.
    public static async Task<(string Token, Guid TenantId, Guid RepositoryId)> CreateAsync(SelfHostedAppFixture app, string purpose)
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        var (clientId, clientSecret) = app.PlatformAdmin;
        using var tokenResponse = await anonymous.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }));
        tokenResponse.EnsureSuccessStatusCode();
        using var platform = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        platform.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());

        var email = $"{purpose}-{Guid.NewGuid():N}@desktop.e2e";
        var tenant = await (await platform.PostAsJsonAsync("api/tenants", new
        {
            name = $"{purpose} {Guid.NewGuid():N}"[..20],
            administratorEmail = email,
            administratorDisplayName = $"{purpose} admin",
            repositoryName = purpose,
        })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var password = tenant.GetProperty("tenantAdministrator").GetProperty("password").GetString()!;
        return (await Ui.GetUserTokenAsync(app.BaseUrl, email, password), tenant.GetProperty("id").GetGuid(),
            tenant.GetProperty("repository").GetProperty("id").GetGuid());
    }

    // The TestModule activated in that tenant: the licence filed in its repository, activation (ADR 0740).
    public static async Task ActivateAsync(SelfHostedAppFixture app, HttpClient http, Guid tenantId, Guid repoId)
    {
        var licenceDoc = await IdOf(await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = "Test module licence" }));
        var version = await (await http.PostAsJsonAsync($"/api/documents/{licenceDoc}/versions", new { fileExtension = ".json" }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(app.SignTestModuleLicence(tenantId))))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{licenceDoc}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync("/api/modules/test-module/license", new { licenseDocumentId = licenceDoc })).EnsureSuccessStatusCode();

        static async Task<Guid> IdOf(HttpResponseMessage response) =>
            (await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
