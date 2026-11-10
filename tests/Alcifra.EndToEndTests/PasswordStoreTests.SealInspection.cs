using System.Net;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

public sealed partial class PasswordStoreTests
{
    // The inspection view (#2291): a Sealed document advertises `seal-inspection`, and the server answers what the CLI's
    // `inspect --document` does, read as stored and never decrypted. Recipients are named as the certificate listing lets
    // the caller see them, and the gap with the audience is marked on the recipient (extra) and listed (missing).
    [Fact]
    public Task A_sealed_document_is_inspected_by_the_server_as_the_cli_would() => WithEnrolmentAsync(async rig =>
    {
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });
        var documents = new Alcifra.ApiClient.DocumentsClient(Alcifra.ApiClient.ApiCore.Over(rig.Anna.Http), () => throw new InvalidOperationException("no reminders here"));
        var childrenHref = Links(await TestJson.Get(rig.Anna.Http, $"/api/documents/{rig.RepoId}"))["children"];
        var id = await documents.CreateSealedDocumentAsync(childrenHref, $"Board pack {Guid.NewGuid():N}", "minutes"u8.ToArray(), "text/plain", "minutes.txt",
            [new Alcifra.ApiClient.AudienceCandidateInfo(rig.Bob.Id, null, "Bob")]);

        // Followed from the document, never composed.
        var href = Links(await TestJson.Get(rig.Anna.Http, $"/api/documents/{id}"))["seal-inspection"];
        var inspection = await TestJson.Get(rig.Anna.Http, href);
        Assert.Equal("Enveloped", inspection.GetProperty("kind").GetString());
        Assert.True(inspection.GetProperty("audience").GetProperty("matches").GetBoolean());
        Assert.NotEmpty(inspection.GetProperty("asn1").EnumerateArray());

        // The clients' reading of the same answer (the desktop's dialog is built from it).
        var report = await new Alcifra.ApiClient.Inspection.SealInspectionClient(Alcifra.ApiClient.ApiCore.Over(rig.Anna.Http)).GetAsync(href);
        Assert.Equal(Alcifra.ApiClient.Inspection.SealedKind.Enveloped, report.Kind);
        Assert.Equal(2, report.Recipients.Count);
        Assert.True(report.Audience!.Matches);
        Assert.Single(report.Recipients, r => r is { IsKnown: true, HolderName: "Anna" });

        // Anna sees her own holder, not Bob's; an administrator sees both.
        static List<string?> Holders(JsonElement i) =>
            [.. i.GetProperty("recipients").EnumerateArray().Select(r => r.GetProperty("holderName").GetString()).Order()];
        Assert.Equal([null, "Anna"], Holders(inspection));
        Assert.Equal(["Anna", "Bob"], Holders(await TestJson.Get(rig.Admin, href)));

        // Bob leaves the audience with no re-seal: his recipient is marked out of the audience, and still in the envelope.
        var audiences = new Alcifra.ApiClient.ContentAudienceClient(Alcifra.ApiClient.ApiCore.Over(rig.Anna.Http));
        var seen = await audiences.GetAsync(Links(await TestJson.Get(rig.Anna.Http, $"/api/documents/{id}"))["audience"]);
        await audiences.SaveAsync(seen, [.. seen.Members.Where(m => m.UserId != rig.Bob.Id).Select(Alcifra.ApiClient.AudienceChange.Of)]);
        var after = await TestJson.Get(rig.Admin, href);
        Assert.False(after.GetProperty("audience").GetProperty("matches").GetBoolean());
        var extra = Assert.Single(after.GetProperty("recipients").EnumerateArray(), r => !r.GetProperty("inAudience").GetBoolean());
        Assert.Equal("Bob", extra.GetProperty("holderName").GetString());

        // Whoever may not read the content is not told whom it is sealed to.
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = false });
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Bob.Http.GetAsync(href)).StatusCode);
        return 0;
    });
}
