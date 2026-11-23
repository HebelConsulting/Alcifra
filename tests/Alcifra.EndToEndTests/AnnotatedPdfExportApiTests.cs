using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Reader = UglyToad.PdfPig;

namespace Alcifra.EndToEndTests;

// ADR 1106, slice 1: the ephemeral export — a PDF version served with its annotations as REAL PDF annotations,
// built on demand and never stored; the rel advertised where the tenant's doors serve plaintext; a non-PDF
// version refused by name.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-3")]
public class AnnotatedPdfExportApiTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_pdf_version_exports_with_its_annotations_as_real_pdf_annotations()
    {
        var api = await AuthedClientAsync();
        var (docId, versionId) = await SeedConfirmedPdfAsync(api);

        // The version resource advertises the export where the doors serve plaintext (ADR 0543).
        var version = await GetJson(api, $"/api/documents/{docId}/versions/{versionId}");
        var rel = version.GetProperty("links").EnumerateArray()
            .SingleOrDefault(link => link.GetProperty("rel").GetString() == "annotated-pdf");
        Assert.NotEqual(default, rel);
        var url = rel!.GetProperty("href").GetString()!;
        Assert.EndsWith($"/api/documents/{docId}/versions/{versionId}/annotated-pdf", url, StringComparison.Ordinal);

        // Two marks of different kinds (the stored kinds of ADR 0525).
        var annotationsUrl = $"/api/documents/{docId}/versions/{versionId}/annotations";
        await PostJson(api, annotationsUrl, new { pageIndex = 0, kind = 0, positionX = 0.5, positionY = 0.5, text = "Look here", color = "#FFEB3B" });
        await PostJson(api, annotationsUrl, new { pageIndex = 0, kind = 2, positionX = 0.1, positionY = 0.2, width = 0.3, height = 0.2, text = "", color = "#4FC3F7" });
        // A signature (kind 8) travels as toggleable ink carrying its name (ADR 1106) — only FLATTENING binds.
        await PostJson(api, annotationsUrl, new { pageIndex = 0, kind = 8, positionX = 0.4, positionY = 0.6, text = "Anna Meier", color = "#8BC34A", points = "0.4,0.6 0.45,0.65 0.5,0.62" });

        var response = await api.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("(annotations).pdf", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);

        // A capable parser reads the two marks back — what any viewer will show and hide.
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var pdf = Reader.PdfDocument.Open(bytes);
        var annotations = pdf.GetPage(1).GetAnnotations().ToList();
        Assert.Equal(3, annotations.Count);
        Assert.Equal(["Text", "Square", "Ink"], annotations.Select(a => a.Type.ToString()).ToArray());
        Assert.Equal("Anna Meier", annotations[2].Content);

        // The export is ephemeral: nothing was stored — the version list still has its single version.
        var versions = await GetJson(api, $"/api/documents/{docId}/versions");
        Assert.Equal(1, versions.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task A_version_that_is_not_a_pdf_has_no_rel_and_is_refused_by_name()
    {
        var api = await AuthedClientAsync();
        var (docId, versionId) = await SeedConfirmedDocumentAsync(api, ".txt", "body"u8.ToArray());

        var version = await GetJson(api, $"/api/documents/{docId}/versions/{versionId}");
        Assert.DoesNotContain(version.GetProperty("links").EnumerateArray(),
            link => link.GetProperty("rel").GetString() == "annotated-pdf");

        var response = await api.GetAsync($"/api/documents/{docId}/versions/{versionId}/annotated-pdf");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("ANNOTATED_PDF_UNSUPPORTED", problem.GetProperty("errorCode").GetString());
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync()
    {
        var (clientId, secret, _) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        return factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
    }

    private static async Task<(Guid DocId, Guid VersionId)> SeedConfirmedPdfAsync(HttpClient api) =>
        await SeedConfirmedDocumentAsync(api, ".pdf", OnePagePdf());

    private static async Task<(Guid DocId, Guid VersionId)> SeedConfirmedDocumentAsync(HttpClient api, string extension, byte[] content)
    {
        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Export {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await PostJson(api, $"/api/documents/{repoId}/children", new { name = "export-target" })).GetProperty("id").GetGuid();
        var created = await PostJson(api, $"/api/documents/{docId}/versions", new { fileExtension = extension });
        var versionId = created.GetProperty("id").GetGuid();

        using var storage = new HttpClient();
        (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(content))).EnsureSuccessStatusCode();
        await PutJson(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return (docId, versionId);
    }

    // A one-page A4 PDF without text: drawing a string would need a font resolver, and nothing here reads the page.
    private static byte[] OnePagePdf()
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            gfx.DrawRectangle(XBrushes.LightGray, new XRect(40, 40, 515, 60));
        }

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body) =>
        await ReadJson(await client.PostAsJsonAsync(url, body));

    private static async Task<JsonElement> PutJson(HttpClient client, string url, object body) =>
        await ReadJson(await client.PutAsJsonAsync(url, body));

    private static async Task<JsonElement> GetJson(HttpClient client, string url) => await ReadJson(await client.GetAsync(url));

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
