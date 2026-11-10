using System.Security.Cryptography;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

// A field's value completing from a module's vocabulary (ABI 2.2, core #2288), over the real wire and the real loader:
// while the module is active the mask's field carries a `vocabulary` rel beside `values`, and following it with ?q=
// answers coded items with labels; once the license lapses the mask survives (ADR 0740) but the rel is withdrawn,
// because the module's route would answer 404 (ADR 0543).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class FieldVocabularyTests
{
    private readonly E2EApiFactory _factory;

    public FieldVocabularyTests(E2EApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> IssuerFieldAsync(HttpClient client) =>
        (await TestJson.Get(client, $"/api/masks/{Alcifra.TestModule.TestModule.CertificateMaskId}"))
            .GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Issuer");

    private static string? Href(JsonElement field, string rel) =>
        field.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == rel)
            .Select(l => l.GetProperty("href").GetString())
            .SingleOrDefault();

    [Fact]
    public async Task An_active_modules_field_advertises_its_vocabulary_and_withdraws_it_on_lapse()
    {
        var rig = await TestModuleRig.CreateAsync(_factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);

            // ACTIVE: the vocabulary beside the values already filed, so a client not knowing the rel completes as before.
            var issuer = await IssuerFieldAsync(rig.Admin);
            Assert.NotNull(Href(issuer, "values"));
            var vocabulary = Href(issuer, "vocabulary");
            Assert.Equal("/api/test-module/issuers", vocabulary);

            // Followed, not composed: the advertised address plus a query, matched on the label as well as the code.
            var answer = await TestJson.Get(rig.Admin, $"{vocabulary}?q=zurich");
            var item = Assert.Single(answer.GetProperty("items").EnumerateArray());
            Assert.Equal("AMC-ZRH", item.GetProperty("value").GetString());
            Assert.Equal("Aeromedical Center Zurich", item.GetProperty("label").GetString());

            // A field the module declares no vocabulary for advertises none.
            var voided = (await TestJson.Get(rig.Admin, $"/api/masks/{Alcifra.TestModule.TestModule.CertificateMaskId}"))
                .GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "Temporarily void");
            Assert.Null(Href(voided, "vocabulary"));

            // LAPSED: the mask and its values stay; the vocabulary, whose route now answers 404, is withdrawn.
            var lapsedDocId = await TestModuleRig.FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-40)), vendorKey);
            await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = lapsedDocId });

            issuer = await IssuerFieldAsync(rig.Admin);
            Assert.NotNull(Href(issuer, "values"));
            Assert.Null(Href(issuer, "vocabulary"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }
}
