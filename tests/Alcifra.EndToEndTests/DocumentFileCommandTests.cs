using System.Security.Cryptography;
using System.Text;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;
using Alcifra.Domain.Masks;

namespace Alcifra.EndToEndTests;

/// <summary>
/// <c>alcifra document file</c> (ADR 1040): a local file filed under a folder with a mask and its fields, the way the
/// kiosk seeds a module's demo documents. Driven through the CLI's own filing code against the real API and object
/// storage, because the order of the acts (mask before content) is what the server's answers depend on.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class DocumentFileCommandTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_file_is_filed_with_its_mask_and_fields_and_a_rerun_changes_nothing()
    {
        var (http, api, maskName, repositoryId) = await ArchiveAsync();
        var name = $"Clip {Guid.NewGuid():N}"[..13];
        var bytes = Encoding.UTF8.GetBytes("not really a video");
        DocumentFiling.FieldValue[] fields =
        [
            DocumentFiling.ParseField("Description=A short = clip"),
            DocumentFiling.ParseField("Tags=demo"),
            DocumentFiling.ParseField("Tags=welcome"),
        ];

        var filed = await DocumentFiling.FileAsync(api, new Hypermedia(api), repositoryId, name, ".mp4", bytes, maskName, fields, CancellationToken.None);
        Assert.Equal(LicenceFiling.Outcome.Filed, filed.Outcome);

        // The mask survived the finalize (which reclassifies only a generic mask), and the fields are the ones given:
        // the first '=' splits, and a repeated name is a list field's second value.
        var detail = await TestJson.Get(http, $"/api/documents/{filed.DocumentId}/detail");
        Assert.Equal(maskName, detail.GetProperty("maskName").GetString());
        var values = detail.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("fieldDefinitionId").GetGuid(), f => f.GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToList());
        Assert.Contains(values.Values, v => v.SequenceEqual(["A short = clip"]));
        Assert.Contains(values.Values, v => v.SequenceEqual(["demo", "welcome"]));
        Assert.Equal([Hash(bytes)], await ConfirmedHashesAsync(http, filed.DocumentId));

        // The kiosk's seed runs this every night and after a half-finished run: the same bytes are left alone.
        var again = await DocumentFiling.FileAsync(api, new Hypermedia(api), repositoryId, name, ".mp4", bytes, maskName, fields, CancellationToken.None);
        Assert.Equal((filed.DocumentId, LicenceFiling.Outcome.Unchanged), again);
        Assert.Single(await ConfirmedHashesAsync(http, filed.DocumentId));
    }

    [Fact]
    public async Task An_unknown_field_is_refused_before_anything_is_written()
    {
        var (http, api, maskName, repositoryId) = await ArchiveAsync();
        var name = $"Clip {Guid.NewGuid():N}"[..13];

        var refused = await Assert.ThrowsAsync<CliException>(() => DocumentFiling.FileAsync(
            api, new Hypermedia(api), repositoryId, name, ".mp4", [1, 2, 3], maskName,
            [DocumentFiling.ParseField("Colour=blue")], CancellationToken.None));

        Assert.Contains("no field 'Colour'", refused.Message, StringComparison.Ordinal);
        var children = await TestJson.Get(http, $"/api/documents/{repositoryId}/children");
        Assert.DoesNotContain(children.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task A_field_without_its_mask_and_a_field_without_a_name_are_refused()
    {
        Assert.Throws<CliException>(() => DocumentFiling.ParseField("=value"));
        var (_, api, _, repositoryId) = await ArchiveAsync();
        await Assert.ThrowsAsync<CliException>(() => DocumentFiling.FileAsync(
            api, new Hypermedia(api), repositoryId, "x", ".txt", [1], null, [DocumentFiling.ParseField("A=b")], CancellationToken.None));
    }

    private async Task<(HttpClient Http, AlcifraApi Api, string MaskName, Guid RepositoryId)> ArchiveAsync()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"filer-{Guid.NewGuid():N}@e2e.local";
        const string password = "filer1234";
        await factory.SeedUserAsync(tenantId, email, password, "Filer", canManageRepositories: true);
        await factory.GrantTenantAdminAsync(email);   // carries CanManageMasks
        var http = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));

        // The create endpoint takes the data type as its enum ORDINAL (the asymmetry the list-field tests document).
        var maskName = $"Clip {Guid.NewGuid():N}";
        await TestJson.Post(http, "/api/masks", new
        {
            name = maskName,
            fields = new object[]
            {
                new { name = "Description", dataType = (int)FieldDataType.Text, isRequired = false, isList = false },
                new { name = "Tags", dataType = (int)FieldDataType.Text, isRequired = false, isList = true },
            },
        });
        var repository = await TestJson.Post(http, "/api/repositories", new { name = $"clips-{Guid.NewGuid():N}" });
        return (http, new AlcifraApi(http), maskName, repository.GetProperty("id").GetGuid());
    }

    private static async Task<List<string>> ConfirmedHashesAsync(HttpClient http, Guid documentId)
    {
        var versions = await TestJson.Get(http, $"/api/documents/{documentId}/versions");
        return [.. versions.GetProperty("versions").EnumerateArray()
            .Where(v => v.GetProperty("status").GetString() == "Confirmed")
            .Select(v => v.GetProperty("sha256Hash").GetString()!)];
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
