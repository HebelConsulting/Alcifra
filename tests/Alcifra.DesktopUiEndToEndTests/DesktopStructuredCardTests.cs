using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// A contact or an appointment shows as a card in the preview, chosen by its mask (#2215): the editor's own form, read-only,
// and a pencil that edits it and refreshes the card. A .vcf the archive filed as a Basic Entry keeps its text preview.
[Collection(UiCollection.Name)]
public class DesktopStructuredCardTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task A_contact_and_an_appointment_show_as_cards_and_a_plain_vcf_keeps_its_text()
    {
        DesktopClientOptions.ApiBaseUrl = app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(app.BaseUrl);
        var api = new AlcifraApiClient(token);
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var run = Guid.NewGuid().ToString("N")[..8];
        var book = await FolderAsync(http, repoId, $"Book {run}", "addressbook");
        var calendar = await FolderAsync(http, repoId, $"Calendar {run}", "calendar");
        var plain = await FolderAsync(http, repoId, $"Plain {run}", "folder");

        var vcard = $"BEGIN:VCARD\r\nVERSION:4.0\r\nUID:card-{run}\r\nFN:Anna Muster\r\nN:Muster;Anna;;;\r\nORG:Example AG\r\nEMAIL;TYPE=work:anna@example.com\r\nEND:VCARD\r\n";
        await FileAsync(http, book, $"Anna {run}", ".vcf", vcard);
        await FileAsync(http, plain, $"Loose {run}", ".vcf", vcard);
        var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:appt-{run}\r\nDTSTAMP:20261107T080000Z\r\nDTSTART:20261110T090000Z\r\nDTEND:20261110T100000Z\r\nSUMMARY:Review {run}\r\nLOCATION:Room 4\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await FileAsync(http, calendar, $"Review {run}", ".ics", ics);

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);

        // The contact: its form, read-only, with the pencil.
        await vm.OpenFolderAsync($"/api/documents/{book}");
        vm.SelectedItem = vm.Items.Single(i => !i.IsFolder);
        await WaitForAsync(() => vm.Preview.Card?.Contact is not null);
        var contact = vm.Preview.Card!.Contact!;
        Assert.Equal("Anna", contact.GivenName);
        Assert.Equal("Example AG", contact.Organization);
        Assert.False(contact.CanEdit, "the card's form is read-only");
        Assert.True(vm.Preview.Card.CanEdit, "an administrator may edit it, so the pencil is offered");

        // The pencil: a fresh copy for the editor, saved, and the card read again.
        var edit = await vm.Preview.Card.OpenForEdit!();
        var form = Assert.IsType<ContactEditViewModel>(edit!.Form);
        Assert.True(form.CanEdit);
        form.Organization = $"Renamed {run}";
        await edit.SaveAsync(form);
        await WaitForAsync(() => vm.Preview.Card?.Contact?.Organization == $"Renamed {run}");

        // The appointment.
        await vm.OpenFolderAsync($"/api/documents/{calendar}");
        vm.SelectedItem = vm.Items.Single(i => !i.IsFolder);
        await WaitForAsync(() => vm.Preview.Card?.Appointment is not null);
        Assert.Equal($"Review {run}", vm.Preview.Card!.Appointment!.Summary);
        Assert.Equal("Room 4", vm.Preview.Card.Appointment.Location);

        // A .vcf filed in an ordinary folder is a Basic Entry, not a contact: whatever its preview is, it is not a card.
        await vm.OpenFolderAsync($"/api/documents/{plain}");
        vm.SelectedItem = vm.Items.Single(i => !i.IsFolder);
        await WaitForAsync(() => vm.DetailTitle == $"Loose {run}" && (vm.Preview.HasPreview || vm.Preview.PreviewPlaceholder is not null));
        Assert.Null(vm.Preview.Card);
    }

    private static async Task<Guid> FolderAsync(HttpClient http, Guid parent, string name, string folderMask) =>
        (await (await http.PostAsJsonAsync($"/api/documents/{parent}/children", new { name, folderMask })).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private static async Task FileAsync(HttpClient http, Guid folder, string name, string extension, string text)
    {
        var id = (await (await http.PostAsJsonAsync($"/api/documents/{folder}/children", new { name })).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = await (await http.PostAsJsonAsync($"/api/documents/{id}/versions", new { fileExtension = extension }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(text)))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{id}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "timed out waiting for the card");
    }
}
