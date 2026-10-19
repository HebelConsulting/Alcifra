using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The check-in comment dialog opened for real (ADR 0952): the desktop client's `--checkin-comment-test` hook
// drives MainWindow.OnCheckoutCheckIn headlessly, so CheckInCommentDialog appears, is typed into and answered —
// then this asserts the stored outcome through the API. Each test checks out and stashes a run-unique document
// of its own (ADR 0884).
[Collection(UiCollection.Name)]
public class DesktopCheckoutCommentDialogTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCheckoutCommentDialogTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Check_in_through_the_dialog_files_the_typed_comment_on_the_new_version()
    {
        var (api, token, doc) = await CheckedOutWithStashAsync("cicom");

        var (exitCode, output) = await DesktopProc.RunAsync("--checkin-comment-test", _app.BaseUrl, token, doc.Id.ToString(), "  Fixed the totals  ");

        AssertHookPassed(exitCode, output);
        Assert.False((await api.Documents.GetChildrenAsync(doc.ParentHref)).Single(n => n.Id == doc.Id).CheckedOut);
        var latest = (await api.Versions.GetVersionsAsync(doc.VersionsHref)).MaxBy(v => v.VersionNumber ?? 0)!;
        Assert.Equal(2, latest.VersionNumber);
        Assert.Equal("Fixed the totals", latest.Comment);
    }

    [Fact]
    public async Task Cancel_in_the_dialog_aborts_the_check_in()
    {
        var (api, token, doc) = await CheckedOutWithStashAsync("cican");

        var (exitCode, output) = await DesktopProc.RunAsync("--checkin-comment-test", _app.BaseUrl, token, doc.Id.ToString(), "--cancel");

        AssertHookPassed(exitCode, output);
        Assert.True((await api.Documents.GetChildrenAsync(doc.ParentHref)).Single(n => n.Id == doc.Id).CheckedOut);
        Assert.Single(await api.Versions.GetVersionsAsync(doc.VersionsHref));
    }

    private static void AssertHookPassed(int exitCode, string output)
    {
        Assert.True(exitCode == 0, $"The hook exited {exitCode}. Output:\n{output}");
        Assert.Contains("The comment dialog opened", output, StringComparison.Ordinal);
        Assert.False(output.Contains("FAILED", StringComparison.Ordinal), $"The hook reported FAILED. Output:\n{output}");
    }

    private async Task<(SimplArchiveApiClient Api, string Token, (Guid Id, string ParentHref, string VersionsHref) Doc)> CheckedOutWithStashAsync(string prefix)
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(token);

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"{prefix}-{Guid.NewGuid():N}.txt";
        await api.Documents.UploadFileAsync(repo.Href("children"), fileName, Encoding.UTF8.GetBytes("original"));
        var doc = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
        await api.Checkout.CheckOutViaDocumentAsync(doc.Href("self"));
        await api.Checkout.SaveWorkingCopyAsync((await api.Checkout.GetCheckoutsAsync()).Single(c => c.Id == doc.Id), Encoding.UTF8.GetBytes("edited"));
        return (api, token, (doc.Id, repo.Href("children"), doc.Href("versions")));
    }
}
