using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Api.ProtocolDevices;

namespace Alcifra.EndToEndTests;

// #2161, ADR 1051: the requests a mounted volume really sent for an upload, replayed one at a time. Each fails when the
// upload takes too long, and, whatever the machine's speed, when the slow password hash runs more than once.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-3")]
public class WebDavUploadReplayTests
{
    private const string LockBody =
        """<?xml version="1.0"?><D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner>replay</D:owner></D:lockinfo>""";

    private readonly E2EApiFactory _factory;

    public WebDavUploadReplayTests(E2EApiFactory factory) => _factory = factory;

    private static string WebDav => "/" + global::Alcifra.Presentation.WireNames.WebDavSegment;

    [Theory]
    [InlineData("finder-upload.tsv", 15, "", "Alcifra-Flyer-DE.pdf,Alcifra-Flyer-EN.pdf,Alcifra-Flyer-ES.pdf,Alcifra-Flyer-IT.pdf")]
    [InlineData("finder-hundred-small-files.tsv", 60, "", "note-001.txt,note-050.txt,note-100.txt")]
    [InlineData("finder-nested-folder.tsv", 15, "2026/q1/invoices/", "file-1.txt,file-2.txt,file-3.txt")]
    public async Task A_recorded_upload_finishes_in_time_and_verifies_the_password_once(
        string recording, int limitSeconds, string listedBelow, string expectedFiles)
    {
        var limit = TimeSpan.FromSeconds(limitSeconds);
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"replay-{Guid.NewGuid():N}@e2e.local";
        const string name = "Replay User";
        await _factory.SeedUserAsync(tenantId, email, "replay-1234", name);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "replay-1234"));
        var devices = (await TestJson.Get(api, "/api/me")).GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "protocolDevices").GetProperty("href").GetString()!;
        var enrolled = await TestJson.Post(api, devices, new { name = "Replay", mail = false, files = true, calendars = false, contacts = false });
        var credentials = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{enrolled.GetProperty("password").GetString()}")));

        using var dav = _factory.CreateClient();
        var personal = Uri.EscapeDataString(name);
        var folder = $"replay-{Guid.NewGuid():N}";
        var rows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Recordings", recording));
        var verified = _factory.Services.GetRequiredService<VerifiedDevicePasswords>();
        var verifiedBefore = verified.Remembered;
        var spaces = _factory.Services.GetRequiredService<Alcifra.Api.WebDav.CheckedPersonalSpaces>();
        var spacesBefore = spaces.Checks;

        (await SendAsync(dav, credentials, "MKCOL", $"{WebDav}/{personal}/My%20Documents", new Dictionary<string, string>(), null)).Dispose();

        var locks = new Dictionary<string, string>();
        var refused = new List<string>();
        var clock = Stopwatch.StartNew();
        foreach (var row in rows)
        {
            var columns = row.Split('\t');
            var (method, length) = (columns[0], int.Parse(columns[1]));
            var path = WebDav + columns[2].Replace("{personal}", personal).Replace("{folder}", folder);

            var headers = new Dictionary<string, string>();
            HttpContent? content = null;
            switch (method)
            {
                case "PROPFIND":
                    headers["Depth"] = path.EndsWith('/') ? "1" : "0";
                    break;
                case "PUT":
                    content = new ByteArrayContent(new byte[length]);
                    if (locks.TryGetValue(path, out var held))
                    {
                        headers["If"] = $"({held})";
                    }

                    break;
                case "LOCK":
                    content = new StringContent(LockBody, Encoding.UTF8, "application/xml");
                    headers["Depth"] = "0";
                    break;
                case "UNLOCK":
                    headers["Lock-Token"] = locks.Remove(path, out var token) ? token : "<urn:uuid:00000000-0000-0000-0000-000000000000>";
                    break;
            }

            using var response = await SendAsync(dav, credentials, method, path, headers, content);
            if (method == "LOCK" && response.Headers.TryGetValues("Lock-Token", out var tokens))
            {
                locks[path] = tokens.Single();
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused.Add($"{method} {columns[2]} → {(int)response.StatusCode}");
            }
        }

        clock.Stop();
        Assert.Empty(refused);
        Assert.True(clock.Elapsed < limit, $"{rows.Length} requests took {clock.Elapsed.TotalSeconds:F1} s, over the limit of {limitSeconds} s.");
        Assert.Equal(1, verified.Remembered - verifiedBefore);
        Assert.Equal(1, spaces.Checks - spacesBefore);

        using var listing = await SendAsync(dav, credentials, "PROPFIND", $"{WebDav}/{personal}/My%20Documents/{folder}/{listedBelow}",
            new Dictionary<string, string> { ["Depth"] = "1" }, null);
        var listed = await listing.Content.ReadAsStringAsync();
        foreach (var file in expectedFiles.Split(','))
        {
            Assert.Contains(file, listed);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient dav, AuthenticationHeaderValue credentials, string method, string path,
        IReadOnlyDictionary<string, string> headers, HttpContent? content)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        request.Headers.Authorization = credentials;
        foreach (var (header, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        return await dav.SendAsync(request);
    }
}
