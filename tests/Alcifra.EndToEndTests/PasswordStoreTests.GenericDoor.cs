using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Domain.Masks;

namespace Alcifra.EndToEndTests;

// #1931 (owner decision 2026-11-02): a password entry is made only through its store's own door, which carries ADR
// 0982's rule; the generic document door refuses it, named or implied, and its New ▸ offer does not list it.
public sealed partial class PasswordStoreTests
{
    [Fact]
    public Task The_generic_document_door_makes_no_password_entry() => WithEnrolmentAsync(async rig =>
    {
        var team = await SharedStoreAsync(rig);
        var (carolId, _, carol, _) = await KeylessReaderAsync(rig);
        await DeclareAudienceAsync(rig.Anna.Http, team.Id, (rig.Anna.Id, true), (carolId, false));
        var children = $"/api/documents/{team.Id}/children";

        // Not offered by New ▸ on a password folder's row, for anybody: entries have their own door.
        var folderName = $"Folder {Guid.NewGuid():N}";
        using (var made = await rig.Anna.Http.PostAsJsonAsync(Links(await TestJson.Get(rig.Anna.Http, team.Links.Href("self")!))["password-folders"], new { name = folderName }))
        {
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        foreach (var caller in new[] { rig.Anna.Http, carol })
        {
            var row = (await TestJson.Get(caller, children)).GetProperty("children").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == folderName);
            Assert.DoesNotContain(row.GetProperty("admits").EnumerateArray(),
                a => a.GetProperty("maskId").GetGuid() == WellKnownMaskIds.PasswordEntry);
        }

        // Named: refused with the reason, even for a reader who holds a key.
        using (var named = await rig.Anna.Http.PostAsJsonAsync(children, new { name = "Named entry", maskId = WellKnownMaskIds.PasswordEntry }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, named.StatusCode);
            Assert.Equal("PASSWORD_ENTRY_USES_ITS_STORE", (await named.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }

        // Implied (an item-to-be, the first step of an upload, which a store would file as an entry): refused the same.
        using (var implied = await carol.PostAsJsonAsync(children, new { name = "Implied entry" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, implied.StatusCode);
            Assert.Equal("PASSWORD_ENTRY_USES_ITS_STORE", (await implied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }

        Assert.False(await AnyDocumentNamedAsync("Named entry"));
        Assert.False(await AnyDocumentNamedAsync("Implied entry"));
        return 0;
    });
}
