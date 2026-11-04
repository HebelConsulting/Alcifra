using System.Text;
using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.EndToEndTests;

// Fetching an entry and opening it are two steps (Mobile #244): a host finishes the server's part before it touches the key,
// because over NFC the key leaves the field if the network comes between the tap and the PIN.
public sealed partial class PasswordStoreTests
{
    [Fact]
    public Task An_entry_is_fetched_sealed_and_opened_later_with_no_further_read() => WithEnrolmentAsync(async rig =>
    {
        var folder = await SharedStoreAsync(rig);
        var client = rig.Anna.Client;
        var entry = await client.CreateEntryAsync(folder, $"fetch-{Guid.NewGuid():N}", PasswordEntryContent.Of("first-secret", "anna"));

        var fetched = await client.FetchSealedAsync(await client.GetEntryAsync(entry.Links.Href("self")!));
        Assert.DoesNotContain("first-secret", Encoding.UTF8.GetString(fetched.SealedBytes), StringComparison.Ordinal);

        // The server moves on; what was fetched still opens as it was, so opening asked the server nothing.
        var current = await client.OpenAsync(await client.GetEntryAsync(entry.Links.Href("self")!));
        await client.SaveEntryAsync(current, PasswordEntryContent.Of("second-secret", "anna"));

        var opened = await client.OpenFetchedAsync(fetched);
        Assert.Equal("first-secret", opened.Content.Password);
        Assert.Equal(entry.Id, opened.Entry.Id);

        var after = await client.OpenAsync(await client.GetEntryAsync(entry.Links.Href("self")!));
        Assert.Equal("second-secret", after.Content.Password);
        return true;
    });
}
