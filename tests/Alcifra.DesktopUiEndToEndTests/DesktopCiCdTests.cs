using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.ApiClient.CiCd;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Presentation;
using Alcifra.UnitTests.PasswordStore;

namespace Alcifra.UiEndToEndTests;

// The desktop's CI/CD store (ADR 1084): the editor refuses what the shared rules refuse before anything is sealed, and a
// secret in the workbench preview shows its names before the key and its values after one open, against the real
// application. The hardware key is a key held in memory, where the card would be.
[Collection(UiCollection.Name)]
public sealed class DesktopCiCdTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCiCdTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public void The_editor_says_what_stops_a_save_and_names_a_dropped_file_as_a_key()
    {
        var editor = new CiCdEntryEditorViewModel(null, (_, _) => Task.CompletedTask);
        Assert.False(editor.CanSave);
        Assert.Contains(Alcifra.Localization.Strings.Get("CiCdProblemNoName"), editor.Problems);
        Assert.Contains(Alcifra.Localization.Strings.Get("CiCdProblemNoTarget"), editor.Problems);

        editor.Name = "Signing";
        editor.TargetsText = "github:HebelConsulting/Alcifra";
        editor.Keys[0].Name = "GITHUB_TOKEN";
        Assert.Contains(Alcifra.Localization.Strings.Get("CiCdProblemInvalidKeyName"), editor.Problems);

        editor.Keys[0].Name = "MACOS_CERT_PASSWORD";
        Assert.True(editor.CanSave);

        // A dropped file becomes a file key named after it.
        editor.AddFile("Developer ID.p12", [1, 2, 3]);
        Assert.Equal("DEVELOPER_ID_P12", editor.Keys[1].Name);
        Assert.Equal(CiCdKeyKind.File, editor.Keys[1].Kind);
        Assert.True(editor.CanSave);
        Assert.Equal(["DEVELOPER_ID_P12"], editor.Content().Secrets.Where(s => s.IsFile).Select(s => s.Name));
    }

    [Fact]
    public async Task A_secret_in_the_preview_shows_its_names_before_the_key_and_its_values_after_one_open()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var (restore, key) = await Alcifra.TestSupport.TestKeyEnrolment.EnrolKeyAsync(http, SelfHostedAppFixture.AdminEmail, $"CI key {Guid.NewGuid():N}"[..14]);
        await using var _ = restore;

        // A run-unique store in the demo repository (ADR 0884), made through the ordinary children door.
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repo = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var created = await (await http.PostAsJsonAsync($"/api/documents/{repo}/children",
            new { name = $"Pipelines {Guid.NewGuid():N}"[..18], maskId = Alcifra.Domain.Masks.WellKnownMaskIds.CiCdStore })).Content.ReadFromJsonAsync<JsonElement>();

        var client = new CiCdStoreClient(ApiCore.Over(http), new KeyOpener(key));
        var store = await client.GetStoreAsync((await client.ListStoresAsync()).Single(s => s.Id == created.GetProperty("id").GetGuid()).Href);
        var entry = await client.CreateAsync(store, "Signing", new CiCdEntryContent(
            ["github:HebelConsulting/Alcifra"],
            [new CiCdSecretValue("MACOS_CERT_PASSWORD", "hunter2"u8.ToArray(), IsFile: false), new CiCdSecretValue("MACOS_CERT_P12_BASE64", [0x30, 0x82], IsFile: true)],
            [new CiCdVariable("POSTPONED", "false")]));

        var screens = new List<bool>();
        var preview = new CiCdEntryPreviewViewModel(entry, client,
            new SecretFieldServices(new FakeClipboard(), _ => Task.CompletedTask, new TestShell(), new SecretScreens(screens.Add)));

        // Before the key: targets, names and variables, and no value anywhere.
        Assert.True(preview.IsClosed);
        Assert.Equal(["github:HebelConsulting/Alcifra"], preview.Targets);
        Assert.Equal(["MACOS_CERT_PASSWORD", "MACOS_CERT_P12_BASE64"], preview.SecretKeys);
        Assert.Equal("false", Assert.Single(preview.Variables).Display);
        Assert.Empty(preview.Secrets);

        await preview.OpenCommand.ExecuteAsync(null);

        Assert.True(preview.IsOpened);
        var secret = Assert.Single(preview.Secrets);
        Assert.NotEqual("hunter2", secret.Display);   // hidden until the eye is held
        secret.Press();
        Assert.Equal("hunter2", secret.Display);
        Assert.Contains(true, screens);               // the window kept out of capture while it shows
        secret.Release();
        Assert.Equal("MACOS_CERT_P12_BASE64", Assert.Single(preview.Files).Name);
    }

    // Opens an envelope the way a host's opener does: the S/MIME step, the in-memory key, the shared payload step.
    private sealed class KeyOpener(X509Certificate2 key) : IEnvelopeOpener
    {
        public Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType)
        {
            var cms = new EnvelopedCms();
            cms.Decode(SmimeEnvelope.Cms(envelope));
            try
            {
                cms.Decrypt(new X509Certificate2Collection(key));
            }
            catch (CryptographicException e)
            {
                throw new EnvelopeNotOpenedException("it is not sealed to this key", e);
            }

            return Task.FromResult(SmimeEnvelope.Payload(cms.ContentInfo.Content));
        }
    }
}
