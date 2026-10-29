using System.Text;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Offline;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.UiEndToEndTests;

// The offline responder (#1697) answers the server's links from an exported archive, so the shared client's repository
// paths read the same thing online and offline: a repository is exported through the real server, then browsed once
// online and once through the responder, and the answers are compared. The test makes its own repository (ADR 0884).
[Collection(UiCollection.Name)]
public class OfflineResponderParityTests
{
    private readonly SelfHostedAppFixture _app;

    public OfflineResponderParityTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task An_exported_repository_browses_the_same_online_and_offline()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repoName = $"Offline parity {Guid.NewGuid():N}";
        await api.Documents.CreateRepositoryAsync(repoName);
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(r => r.Name == repoName);
        await api.Documents.CreateFolderAsync(repo.Href("children"), "Contracts");
        var payload = Encoding.UTF8.GetBytes($"offline-parity-{Guid.NewGuid():N}");
        await api.Documents.UploadFileAsync(repo.Href("children"), "report.txt", payload);
        var (onlineRoot, _, _) = await api.Documents.GetFolderContentsAsync(repo.Href("children"));
        var contracts = onlineRoot.Single(n => n.Name == "Contracts");
        await api.Documents.UploadFileAsync(contracts.Href("children"), "deal.txt", Encoding.UTF8.GetBytes("deal"));

        var (beforeExport, _, _) = await api.Documents.GetFolderContentsAsync(repo.Href("children"));
        var reportOnline = beforeExport.Single(n => n.Name == "report");
        await api.Documents.PostCommentAsync(reportOnline.Href("chat"), "Please check the totals", null);

        var zip = await api.RepositoryArchive.ExportRepositoryAsync(
            await api.Documents.RelViaSelfAsync(repo.Href("document"), "export"),
            new RepositoryArchiveClient.RepositoryExportOptions(ActiveOnly: false, null, null, null, null, null));
        var responder = new OfflineResponder(await OfflineArchive.OpenAsync(new ZipArchiveStorage(zip)));
        var offline = new DocumentsClient(ApiCore.FromArchive(responder), () => throw new InvalidOperationException("no reminders offline"));
        try
        {
            // The repository listing: the exported root, as the same kind of row.
            var root = Assert.Single(await offline.GetRepositoriesAsync());
            Assert.Equal((repoName, true), (root.Name, root.IsFolder));

            // Its contents: the same names, the same folder/document split, the same order.
            var (online, _, _) = await api.Documents.GetFolderContentsAsync(repo.Href("children"));
            var (offlineRoot, _, _) = await offline.GetFolderContentsAsync(root.Href("children"));
            Assert.Equal(online.Select(n => (n.Name, n.IsFolder)), offlineRoot.Select(n => (n.Name, n.IsFolder)));

            // A folder opens to its own contents.
            var offlineContracts = offlineRoot.Single(n => n.Name == "Contracts");
            Assert.Equal("deal", Assert.Single((await offline.GetFolderContentsAsync(offlineContracts.Href("children"))).Children).Name);

            // A document: its detail, its current version, and its content byte for byte, through the one content funnel.
            var report = offlineRoot.Single(n => n.Name == "report");   // an upload names it without the extension
            var detail = await offline.GetDocumentDetailAsync(report.Href("self"));
            Assert.Equal("report", detail.Name);
            var system = await offline.GetSystemFieldsAsync(detail.Links!.Href("versions")!);
            Assert.NotNull(system);
            var preview = await new VersionsClient(ApiCore.FromArchive(responder)).GetPreviewFromVersionsAsync(detail.Links.Href("versions")!);
            var (bytes, _, enveloped) = await ApiCore.ReadContentAsync(preview.DownloadUrl!);
            Assert.Equal(payload, bytes);
            Assert.False(enveloped);

            // Its chat: the same messages, by the same author.
            var chatOnline = await api.Documents.GetChatAsync(reportOnline.Href("chat"));
            var chatOffline = await offline.GetChatAsync(report.Href("chat"));
            Assert.Equal(chatOnline.Messages.Where(m => m.Kind == 0).Select(m => (m.Body, m.AuthorName)), chatOffline.Messages.Select(m => (m.Body, m.AuthorName)));
            Assert.Null(chatOffline.MentionableUsersHref);   // nobody is mentioned, or written to, offline

            // What the archive does not carry is simply not offered: no write link on the row.
            Assert.False(report.CanDelete || report.CanMove || report.CanEditIndexData);
        }
        finally
        {
            // The content funnel's session belongs to the live client again.
            _ = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl)).Core;
        }
    }

    // ADR 0959 (#1881): a folder's references read the same online and from its offline export, which brings the
    // referenced document in from another repository, so it opens offline from the reference row.
    [Fact]
    public async Task A_folders_references_read_the_same_online_and_from_its_offline_export()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var homeName = $"Offline refs {Guid.NewGuid():N}";
        var elsewhereName = $"Offline refs elsewhere {Guid.NewGuid():N}";
        await api.Documents.CreateRepositoryAsync(homeName);
        await api.Documents.CreateRepositoryAsync(elsewhereName);
        var repositories = await api.Documents.GetRepositoriesAsync();
        var home = repositories.Single(r => r.Name == homeName);
        var elsewhere = repositories.Single(r => r.Name == elsewhereName);
        await api.Documents.CreateFolderAsync(home.Href("children"), "My documents");
        var myDocuments = (await api.Documents.GetFolderContentsAsync(home.Href("children"))).Children.Single(n => n.Name == "My documents");
        await api.Documents.UploadFileAsync(elsewhere.Href("children"), "agreement.txt", Encoding.UTF8.GetBytes("agreement"));
        var agreement = (await api.Documents.GetFolderContentsAsync(elsewhere.Href("children"))).Children.Single(n => n.Name == "agreement");
        await api.References.CreateReferenceAsync(myDocuments.Href("references"), agreement.Id);

        var online = await api.References.GetReferencesAsync(myDocuments.Href("references"));
        var zip = await api.Core.Http.GetByteArrayAsync(await api.Documents.RelViaSelfAsync(myDocuments.Href("self"), "offlineExport"));
        var responder = new OfflineResponder(await OfflineArchive.OpenAsync(new ZipArchiveStorage(zip)));
        var offlineCore = ApiCore.FromArchive(responder);
        var root = Assert.Single(await new DocumentsClient(offlineCore, () => throw new InvalidOperationException("no reminders offline")).GetRepositoriesAsync());
        var offline = await new ReferencesClient(offlineCore).GetReferencesAsync(root.Href("references"));

        Assert.Equal(online.Select(r => (r.TargetId, r.Name, r.IsFolder)), offline.Select(r => (r.TargetId, r.Name, r.IsFolder)));
        var detail = await new DocumentsClient(offlineCore, () => throw new InvalidOperationException("no reminders offline")).GetDocumentDetailAsync(Assert.Single(offline).Links!.Href("self")!);
        Assert.Equal("agreement", detail.Name);
    }
}
