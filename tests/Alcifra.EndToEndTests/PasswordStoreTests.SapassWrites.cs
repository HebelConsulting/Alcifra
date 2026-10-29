using Alcifra.ApiClient.PasswordStore;
using Alcifra.Sapass;
using Alcifra.Sapass.Infrastructure;

namespace Alcifra.EndToEndTests;

// The tool's WRITE path against a real installation, through the same EntryWriter a command calls: a path whose
// folders do not exist yet, sealed to the store's readers, opened with the owner's key — and a second create at the
// same path refused rather than replacing a secret.
//
// A partial of PasswordStoreTests so it can use that class's enrolment rig rather than growing a second copy of it:
// entries can only be sealed when a reader has an enrolled certificate, and that setup is the rig's whole job.
public sealed partial class PasswordStoreTests
{
    [Fact]
    public Task The_tool_files_a_secret_into_the_folders_its_path_names_and_never_replaces_one() => WithEnrolmentAsync(async rig =>
    {
        _ = await SharedStoreAsync(rig);
        using var session = Session.Over(rig.Anna.Http, new KeyOpener(rig.Anna.Key));
        var catalog = new StoreCatalog(session);

        // "Mail/Banking" does not exist: the writer creates the folders its path names, then files the secret.
        var team = await EntryWriter.StoreForNewEntryAsync(session, "Team", CancellationToken.None);
        var entry = await EntryWriter.CreateAsync(
            session, team, "Mail/Banking/login",
            PasswordEntryContent.Of("hunter2", "anna", "https://bank.example"), refresh: true, CancellationToken.None);

        var rows = await catalog.RowsAsync(await catalog.StoreAsync("Team", CancellationToken.None), refresh: true, CancellationToken.None);
        Assert.Equal("Mail/Banking/login", rows.Single(r => r.Id == entry.Id).Path);
        Assert.Contains(rows, r => r.Path == "Mail/Banking" && r.IsFolder);

        // Sealed to the store's readers and openable by the reader's key: the same client the desktop uses.
        var opened = await rig.Anna.Client.OpenAsync(
            await rig.Anna.Client.GetEntryAsync(rows.Single(r => r.Id == entry.Id).Href));
        Assert.Equal("hunter2", opened.Content.Password);
        Assert.Equal("anna", opened.Content.UserName);

        // Creating NEVER replaces: the same path again is refused by name, and the stored secret is untouched.
        var refused = await Assert.ThrowsAsync<SapassException>(() => EntryWriter.CreateAsync(
            session, team, "Mail/Banking/login",
            PasswordEntryContent.Of("different", "anna"), refresh: true, CancellationToken.None));
        Assert.Contains("already exists", refused.Message);
        Assert.Equal("hunter2", (await rig.Anna.Client.OpenAsync(
            await rig.Anna.Client.GetEntryAsync(rows.Single(r => r.Id == entry.Id).Href))).Content.Password);

        // And rm's own step — a delete under the ETag that was read — removes it from the tree.
        await rig.Anna.Client.DeleteAsync(opened.Entry.Links, opened.Entry.Name, opened.Entry.ETag);
        Assert.DoesNotContain(
            await catalog.RowsAsync(await catalog.StoreAsync("Team", CancellationToken.None), refresh: true, CancellationToken.None),
            r => r.Id == entry.Id);
        return 0;
    });
}
