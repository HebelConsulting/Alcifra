using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.EndToEndTests;

// An at-rest-encrypted object must be REFUSED, not served, on an installation with no encryption service
// (#1499).
//
// WHY THIS EXISTS. A null `Encryption:ServiceUrl` means "fully inert" (ADR 0818), and inert is right for a
// store holding plaintext. An installation whose store already holds CIPHERTEXT and then loses that setting
// is a different case: the encrypting decorator is never installed, so nothing looks at an object's
// `sa-wrapped-dek`, and the bytes are handed on as though they were the document.
//
// MEASURED, and the symptom named nothing. On 2026-09-30 three rolls of the dev stack without `SA_COMPOSE`
// recreated the api from the base compose file alone, dropping the overlay that carried
// `Encryption__ServiceUrl`. For forty minutes a `Strict` tenant's documents were served as raw ciphertext at
// **200**, and the desktop client reported "Preview not supported" — which reads as a broken converter or a
// bad file, never as a setting that had vanished.
//
// IT IS AN E2E TEST BECAUSE THE FIXTURE CANNOT BE FAKED HONESTLY: the guard reads metadata the S3 response
// carries, so proving it needs a real store that round-trips user metadata. The factory's own DI has
// encryption CONFIGURED (it hosts a stub service), so the client under test is constructed directly with the
// flag the composition root would set — which is also what keeps this from asserting the DI wiring twice.
[Collection(E2ECollection.Name)]
// e2e-1, the lighter leg — UiLegBalanceTests requires every class in a split suite to name its leg.
[Trait("Area", "e2e-1")]
public class EncryptedObjectWithoutServiceTests
{
    private readonly E2EApiFactory _factory;

    public EncryptedObjectWithoutServiceTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_object_carrying_a_wrapped_DEK_is_refused_when_nothing_can_unwrap_it()
    {
        var tenantId = Guid.NewGuid();
        var key = $"tenants/{tenantId:D}/2026/{Guid.NewGuid():D}/content.pdf";

        var writer = Client(refuseEncryptedObjects: false);
        await writer.EnsureTenantBucketAsync(tenantId);
        await writer.PutObjectAsync(
            key,
            new MemoryStream(Encoding.UTF8.GetBytes("this stands in for ciphertext")),
            "application/pdf",
            new Dictionary<string, string>
            {
                [AtRestMetadata.WrappedDekKey] = "not-a-real-wrapped-key",
                [AtRestMetadata.KekGenerationKey] = "kek-v1",
            });

        // The client the decorator wraps reads it happily — that is how the decorator gets the bytes it
        // decrypts, and it is why the flag exists rather than an unconditional refusal.
        await using (var asTheDecoratorWould = await writer.GetObjectAsync(key))
        {
            Assert.NotNull(asTheDecoratorWould);
        }

        var guarded = Client(refuseEncryptedObjects: true);

        // BOTH free reads. GetObjectAsync is where ciphertext becomes wrong data that persists — the
        // finalizer's hash, the rendition, the text layout, the search index all come through it.
        await Assert.ThrowsAsync<EncryptedObjectWithoutEncryptionServiceException>(
            () => guarded.GetObjectAsync(key));
        var refusal = await Assert.ThrowsAsync<EncryptedObjectWithoutEncryptionServiceException>(
            () => guarded.GetObjectInfoAsync(key));

        // The message names the SETTING, not the object: the object is not the problem, and a message about
        // it sends an administrator to the store.
        Assert.Contains("Encryption:ServiceUrl", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(key, refusal.ObjectKey);
    }

    [Fact]
    public async Task A_PLAINTEXT_object_passes_through_the_guard_untouched()
    {
        // The anti-vacuous half. A guard that refused everything would pass the test above and take down
        // every installation that has no encryption service — which is most of them.
        var tenantId = Guid.NewGuid();
        var key = $"tenants/{tenantId:D}/2026/{Guid.NewGuid():D}/content.txt";

        var writer = Client(refuseEncryptedObjects: false);
        await writer.EnsureTenantBucketAsync(tenantId);
        await writer.PutObjectAsync(
            key, new MemoryStream(Encoding.UTF8.GetBytes("an ordinary document")), "text/plain");

        var guarded = Client(refuseEncryptedObjects: true);

        var info = await guarded.GetObjectInfoAsync(key);
        Assert.DoesNotContain(AtRestMetadata.WrappedDekKey, info.Metadata.Keys);

        await using var content = await guarded.GetObjectAsync(key);
        Assert.Equal("an ordinary document", await new StreamReader(content).ReadToEndAsync());
    }

    [Fact]
    public async Task A_presigned_READ_of_an_object_carrying_a_wrapped_DEK_is_refused_too()
    {
        // #1843: the reads above refused, but a presign asks the store nothing — so the preview's URL handed the
        // browser the ciphertext. The guard the composition root installs without a service asks first (one HEAD),
        // against the REAL store here because what it reads is metadata the S3 response carries.
        var tenantId = Guid.NewGuid();
        var encrypted = $"tenants/{tenantId:D}/2026/{Guid.NewGuid():D}/content.pdf";
        var plaintext = $"tenants/{tenantId:D}/2026/{Guid.NewGuid():D}/content.txt";
        var missing = $"tenants/{tenantId:D}/2026/{Guid.NewGuid():D}/content.pdf";

        var writer = Client(refuseEncryptedObjects: false);
        await writer.EnsureTenantBucketAsync(tenantId);
        await writer.PutObjectAsync(
            encrypted,
            new MemoryStream(Encoding.UTF8.GetBytes("this stands in for ciphertext")),
            "application/pdf",
            new Dictionary<string, string>
            {
                [AtRestMetadata.WrappedDekKey] = "not-a-real-wrapped-key",
                [AtRestMetadata.KekGenerationKey] = "kek-v1",
            });
        await writer.PutObjectAsync(
            plaintext, new MemoryStream(Encoding.UTF8.GetBytes("an ordinary document")), "text/plain");

        var plain = Client(refuseEncryptedObjects: true);
        var guarded = new EncryptedObjectPresignGuard(plain, plain);

        var download = await Assert.ThrowsAsync<EncryptedObjectWithoutEncryptionServiceException>(
            () => guarded.GetPresignedDownloadUrlAsync(encrypted, TimeSpan.FromMinutes(5), "letterhead.pdf"));
        await Assert.ThrowsAsync<EncryptedObjectWithoutEncryptionServiceException>(
            () => guarded.GetPresignedPreviewUrlAsync(encrypted, TimeSpan.FromMinutes(5), "letterhead.pdf"));
        Assert.Equal("ENCRYPTION_SERVICE_NOT_CONFIGURED", download.ErrorCode);
        Assert.Equal(503, download.ApiStatusCode);

        // The anti-vacuous half: plaintext presigns as before, and so does a key with nothing stored under it —
        // the store's real 404 on the HEAD must read as "nothing to hand out", not as a failure.
        Assert.NotNull(await guarded.GetPresignedPreviewUrlAsync(plaintext, TimeSpan.FromMinutes(5), "a.txt", "text/plain"));
        Assert.NotNull(await guarded.GetPresignedDownloadUrlAsync(missing, TimeSpan.FromMinutes(5)));

        // An upload is not a read: presigned as before.
        Assert.NotNull(await guarded.GetPresignedUploadUrlAsync(encrypted, TimeSpan.FromMinutes(5)));
    }

    /// <summary>The real S3 client against the fixture's store, with the flag the composition root sets.</summary>
    private S3ObjectStorageClient Client(bool refuseEncryptedObjects) => new(
        _factory.Services.GetRequiredService<IOptions<ObjectStorageOptions>>(),
        _factory.Services.GetRequiredService<ILogger<S3ObjectStorageClient>>())
    {
        RefuseEncryptedObjects = refuseEncryptedObjects,
    };
}
