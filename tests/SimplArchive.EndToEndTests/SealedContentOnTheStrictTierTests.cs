using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Storage;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// Sealed module content leaves a strict tenant; plaintext in the same segment does not (ABI 1.20, ADR 0940).
//
// The strict tier refuses every plaintext door, and a module's sealed content — a CMS envelope only its readers' keys open —
// is not plaintext. So the door lets an object through when its segment is declared sealed AND its stored bytes are shaped
// as an envelope, checked at the door: a declaration names a place, and only the object says what is stored there.
//
// An E2E test because the decorator reads a real store (sizes, ranges, metadata); the client is constructed directly with
// a strict mode for a tenant of its own, as EncryptedObjectWithoutServiceTests does, so nothing else in the factory changes.
[Collection(E2ECollection.Name)]
// e2e-1, the lighter leg — UiLegBalanceTests requires every class in a split suite to name its leg.
[Trait("Area", "e2e-1")]
public class SealedContentOnTheStrictTierTests
{
    private const string Segment = "sealed-e2e";
    private readonly E2EApiFactory _factory;

    public SealedContentOnTheStrictTierTests(E2EApiFactory factory) => _factory = factory;

    private sealed class SealingModule : IIndustryModule
    {
        public string ModuleId => "sealed-e2e";
        public string DisplayName => "Sealed";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks { get; } =
        [
            new(Guid.NewGuid(), "Sealed Object", IsFolderMask: false, IsBookable: false, Fields: [])
            {
                ContentSegment = Segment,
                ContentIsSealed = true,
            },
        ];

        public void ConfigureServices(IServiceCollection services) { }
    }

    [Fact]
    public async Task An_envelope_in_a_sealed_segment_leaves_and_plaintext_beside_it_does_not()
    {
        var (client, store, tenantId) = await StrictClientAsync();
        var envelope = $"tenants/{tenantId:D}/{Segment}/{Guid.NewGuid():D}/content.p7m";
        var plaintext = $"tenants/{tenantId:D}/{Segment}/{Guid.NewGuid():D}/content.p7m";
        var archive = $"tenants/{tenantId:D}/2026/{Guid.NewGuid():D}/content.p7m";
        // Written to the store directly: a strict tenant wraps at rest, and this test asks about the door, not the wrap.
        var sealedBytes = Seal("for the readers only");
        await store.PutObjectAsync(envelope, new MemoryStream(sealedBytes), "application/pkcs7-mime");
        await store.PutObjectAsync(plaintext, new MemoryStream(Encoding.UTF8.GetBytes("plain words")), "application/pkcs7-mime");
        await store.PutObjectAsync(archive, new MemoryStream(sealedBytes), "application/pkcs7-mime");

        Assert.NotNull(await client.GetPresignedDownloadUrlAsync(envelope, TimeSpan.FromMinutes(1)));
        await using (var served = await client.GetObjectForClientAsync(envelope, "the test door"))
        using (var copy = new MemoryStream())
        {
            await served.CopyToAsync(copy);
            Assert.Equal(sealedBytes, copy.ToArray());   // exactly what was filed: the core opens nothing
        }

        // The anti-vacuous half: the same tenant still refuses plaintext in the sealed segment, and an envelope OUTSIDE a
        // sealed segment — the door trusts no shape where no module declared one.
        Assert.Null(await client.GetPresignedDownloadUrlAsync(plaintext, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<PlaintextContentRefusedException>(() => client.GetObjectForClientAsync(plaintext, "the test door"));
        await Assert.ThrowsAsync<PlaintextContentRefusedException>(() => client.GetObjectRangeForClientAsync(plaintext, 0, 3, "the test door"));
        Assert.Null(await client.GetPresignedDownloadUrlAsync(archive, TimeSpan.FromMinutes(1)));
    }

    /// <summary>The decorator over the fixture's real store, with a strict mode for a tenant of this test's own.</summary>
    private async Task<(EncryptingObjectStorageClient Client, S3ObjectStorageClient Store, Guid TenantId)> StrictClientAsync()
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"sealed-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Encryption:ServiceUrl"] = "http://127.0.0.1:9",   // never called: nothing here is wrapped at rest
            [$"Encryption:Modes:{tenant.Name}"] = "Strict",
        }).Build();
        var keys = new AtRestKeyService(configuration, _factory.Services.GetRequiredService<IHttpClientFactory>(),
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), _factory.Services.GetRequiredService<ILogger<AtRestKeyService>>());
        var inner = new S3ObjectStorageClient(
            _factory.Services.GetRequiredService<IOptions<ObjectStorageOptions>>(),
            _factory.Services.GetRequiredService<ILogger<S3ObjectStorageClient>>());
        await inner.EnsureTenantBucketAsync(tenant.Id);
        var declarations = new ModuleMaskDeclarations([new ModuleLoader.LoadedModule(new SealingModule(), "test://sealed")]);
        return (new EncryptingObjectStorageClient(inner, keys, urlIssuer: null,
            _factory.Services.GetRequiredService<ILogger<EncryptingObjectStorageClient>>(), declarations), inner, tenant.Id);
    }

    private static byte[] Seal(string text)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=reader", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var envelope = new EnvelopedCms(new ContentInfo(Encoding.UTF8.GetBytes(text)));
        envelope.Encrypt(new CmsRecipient(certificate));
        return envelope.Encode();
    }
}
