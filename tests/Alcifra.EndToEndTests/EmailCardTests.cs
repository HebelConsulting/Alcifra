using System.Net;
using System.Text.Json;
using MimeKit;

namespace Alcifra.EndToEndTests;

// A mail's card for the preview (#2215, ADR 1073): the envelope, the HTML sanitised once on the server (no scripts, no
// handlers, no remote images), its inline image at the card's own address, the text for a client that shows none, and the
// attachments the finalizer filed as children. Only a document the archive classified as an eMail offers it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class EmailCardTests(E2EApiFactory factory)
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task A_mail_reads_as_its_card_sanitised_with_its_inline_image_and_attachments()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"mailcard-{Guid.NewGuid():N}@e2e.local";
        const string password = "mailcard-1234";
        await factory.SeedUserAsync(tenantId, email, password, "Mail Card", canManageRepositories: true, isTenantAdmin: true);
        using var api = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"mailcard-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var run = Guid.NewGuid().ToString("N")[..8];

        var builder = new BodyBuilder
        {
            HtmlBody = "<p onclick=\"steal()\">Hello <b>there</b></p><script>steal()</script>"
                + "<img src=\"http://tracker.example/pixel.gif\"><img src=\"cid:logo@mail\"><a href=\"https://example.com\">link</a>",
            TextBody = "Hello there",
        };
        var logo = builder.LinkedResources.Add("logo.png", Png, new ContentType("image", "png"));
        logo.ContentId = "logo@mail";
        builder.Attachments.Add($"report-{run}.txt", System.Text.Encoding.UTF8.GetBytes("the report"), new ContentType("text", "plain"));
        var message = new MimeMessage { Subject = $"Quarterly {run}", Date = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero), Body = builder.ToMessageBody() };
        message.From.Add(new MailboxAddress("Anna", "anna@e2e.local"));
        message.To.Add(new MailboxAddress("Tom", "tom@e2e.local"));
        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer);

        var mailId = await FileAsync(api, repoId, $"mail-{run}", ".eml", buffer.ToArray());
        var plainId = await FileAsync(api, repoId, $"plain-{run}", ".txt", System.Text.Encoding.UTF8.GetBytes("not a mail"));

        Assert.DoesNotContain(Rels(await TestJson.Get(api, $"/api/documents/{plainId}")), r => r == "email-card");
        var cardHref = Href(await TestJson.Get(api, $"/api/documents/{mailId}"), "email-card");
        var card = await TestJson.Get(api, cardHref);

        Assert.Equal($"Quarterly {run}", card.GetProperty("subject").GetString());
        Assert.Contains("anna@e2e.local", card.GetProperty("from").GetString());
        var html = card.GetProperty("html").GetString()!;
        Assert.Contains("<b>there</b>", html);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Equal("Hello there", card.GetProperty("text").GetString());

        var inlineHref = System.Text.RegularExpressions.Regex.Match(html, "src=\"([^\"]+/email-card/inline/0)\"").Groups[1].Value;
        Assert.Equal(inlineHref, Href(card, "inline-image"));
        using var inline = await api.GetAsync(inlineHref);
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        Assert.Equal("image/png", inline.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await inline.Content.ReadAsByteArrayAsync());

        var attachment = Assert.Single(card.GetProperty("attachments").EnumerateArray());
        Assert.Equal($"report-{run}", attachment.GetProperty("name").GetString());
        Assert.Contains("versions", Rels(attachment));
    }

    private static IEnumerable<string> Rels(JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!);

    private static string Href(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    private static async Task<Guid> FileAsync(HttpClient api, Guid repoId, string name, string extension, byte[] bytes)
    {
        var id = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{id}/versions", new { fileExtension = extension });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{id}/versions/{created.GetProperty("id").GetGuid()}", new { });
        return id;
    }
}
