using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Storage;

namespace Alcifra.EndToEndTests;

// Turning a tenant's encryption OFF must not make its existing content unreadable (#1385).
//
// ADR 0818 calls the mixed state deliberate, and it was implemented in one direction only: a PLAINTEXT object
// on a gated tenant passes through, correctly. The other direction — an ENCRYPTED object on a tenant that is
// no longer gated — was not handled, because the presign asked the GATE before it asked the OBJECT and
// short-circuited on "this tenant does not encrypt". The object was then presigned straight from storage and
// the browser received CIPHERTEXT: 200 OK, bytes that are not the document, nothing logged anywhere.
//
// A mode says what to do with the NEXT write. It is not a claim about what is already stored, and the stored
// object is the only thing that knows whether it is encrypted — it carries the wrapped DEK that says so.
//
// NOT HYPOTHETICAL. The public kiosk's Demo tenant spent a day encrypted, because its config named a key the
// running image did not read and the absent legacy list meant "every tenant" (#1382). The release that
// corrected the gating would, without this, have served ciphertext for every document in the public demo —
// which is why the rollout had to wipe and reseed rather than roll.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class EncryptedObjectOutlivesItsGateTests
{
    private readonly E2EApiFactory _factory;

    public EncryptedObjectOutlivesItsGateTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_encrypted_object_is_served_through_the_door_even_when_its_tenant_no_longer_encrypts()
    {
        // A tenant configuration gives no mode — so it is exactly a tenant whose encryption is OFF, which is
        // both the "never encrypted" and the "no longer encrypts" case. The gate cannot tell them apart, and
        // that is the point: only the object can.
        var tenantId = await _factory.SeedTenantNamedAsync($"NoMode{Guid.NewGuid():N}"[..24]);

        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

        var key = ObjectKeyBuilder.Build(tenantId, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), ".txt");
        await storage.PutObjectAsync(key, new MemoryStream(Encoding.ASCII.GetBytes("stored before the mode changed\n")), "text/plain");

        // FIRST, the anti-vacuous half. While the object carries no wrapped DEK it is plaintext, and a
        // presigned object-storage URL is the right answer — so the assertion below cannot be satisfied by a
        // seam that simply returns the door for everything.
        var plaintext = await storage.GetPresignedDownloadUrlAsync(key, TimeSpan.FromMinutes(5));
        Assert.NotNull(plaintext);
        Assert.True(plaintext!.IsAbsoluteUri, $"a plaintext object must still presign to storage, got {plaintext}");

        // Now make it what an object written while the tenant DID encrypt looks like. Through the product's
        // own attach seam (the one the client-encrypted upload uses), not a raw bucket write, so the state
        // under test is one the application can actually produce.
        await storage.SetObjectMetadataAsync(key, new Dictionary<string, string>
        {
            [AtRestMetadata.WrappedDekKey] = Convert.ToBase64String(new byte[256]),
            [AtRestMetadata.KekGenerationKey] = "kek-v1",
        });

        var encrypted = await storage.GetPresignedDownloadUrlAsync(key, TimeSpan.FromMinutes(5));
        Assert.NotNull(encrypted);
        Assert.False(encrypted!.IsAbsoluteUri,
            $"an encrypted object was presigned straight from storage, so the caller receives CIPHERTEXT: {encrypted}");
        Assert.StartsWith("/api/encrypted-content", encrypted.ToString(), StringComparison.Ordinal);
    }

    // ADR 1003: an object written before the rename carries its wrapping under the sa-* keys, and a WORM-locked one
    // always will. Written encrypted through the product on the gated tenant, then its keys renamed back in the store,
    // it must still decrypt — and a re-wrap's Stamp leaves only the alcifra-* keys.
    [Fact]
    public async Task An_object_wrapped_under_the_keys_before_the_rename_still_decrypts()
    {
        Guid tenantId;
        using (var lookup = _factory.Services.CreateScope())
        {
            tenantId = await lookup.ServiceProvider.GetRequiredService<Alcifra.Infrastructure.Persistence.AlcifraDbContext>()
                .Tenants.IgnoreQueryFilters(["TenantFilter"])
                .Where(t => t.Name == E2EApiFactory.CryptoTenantName).Select(t => t.Id).SingleAsync();
        }

        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();
        var raw = scope.ServiceProvider.GetRequiredService<S3ObjectStorageClient>();
        var key = ObjectKeyBuilder.Build(tenantId, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), ".txt");
        await storage.PutObjectAsync(key, new MemoryStream(Encoding.ASCII.GetBytes("wrapped before the rename\n")), "text/plain");

        var written = (await raw.GetObjectWithMetadataAsync(key)).Metadata;
        Assert.True(written.ContainsKey(AtRestMetadata.WrappedDekKey));
        var renamed = new Dictionary<string, string>
        {
            [AtRestMetadata.LegacyWrappedDekKey] = written[AtRestMetadata.WrappedDekKey],
            [AtRestMetadata.LegacyKekGenerationKey] = written[AtRestMetadata.KekGenerationKey],
            [AtRestMetadata.LegacyKekThumbprintKey] = written[AtRestMetadata.KekThumbprintKey],
        };
        await raw.SetObjectMetadataAsync(key, renamed);
        Assert.False((await raw.GetObjectWithMetadataAsync(key)).Metadata.ContainsKey(AtRestMetadata.WrappedDekKey));

        await using var read = await storage.GetObjectAsync(key);
        Assert.Equal("wrapped before the rename\n", await new StreamReader(read).ReadToEndAsync());

        var restamped = new Dictionary<string, string>(renamed, StringComparer.OrdinalIgnoreCase);
        AtRestMetadata.Stamp(restamped, "w", "g", "t");
        Assert.Equal(
            new[] { AtRestMetadata.WrappedDekKey, AtRestMetadata.KekGenerationKey, AtRestMetadata.KekThumbprintKey }.Order(),
            restamped.Keys.Order());
    }

    [Fact]
    public async Task The_ciphertext_presign_REFUSES_a_plaintext_object_and_serves_an_encrypted_one()
    {
        // The one presign this decorator deliberately does NOT refuse (ADR 0862) — so it is the one that
        // could reopen the door the tier closes. Storage is mixed state, so "the tenant is strict" says
        // nothing about whether a given object is encrypted: a method named for ciphertext, called on
        // plaintext, would hand out exactly what must never leave.
        var tenantId = await _factory.SeedTenantNamedAsync($"Cipher{Guid.NewGuid():N}"[..24]);

        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

        var key = ObjectKeyBuilder.Build(tenantId, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), ".txt");
        await storage.PutObjectAsync(key, new MemoryStream(Encoding.ASCII.GetBytes("plaintext, no wrapped DEK\n")), "text/plain");

        // Plaintext: REFUSED, and loudly. Not null — a null would be a shape the caller might "handle" by
        // falling back to a download URL, which is how this becomes a door.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetPresignedCiphertextUrlAsync(key, TimeSpan.FromMinutes(2)));
        Assert.Contains(AtRestMetadata.WrappedDekKey, refused.Message, StringComparison.Ordinal);

        // Now the same object as one written while the tenant encrypted — the anti-vacuous half, since a
        // method that threw for everything would satisfy the assertion above.
        await storage.SetObjectMetadataAsync(key, new Dictionary<string, string>
        {
            [AtRestMetadata.WrappedDekKey] = Convert.ToBase64String(new byte[256]),
            [AtRestMetadata.KekGenerationKey] = "kek-v1",
        });

        var ciphertext = await storage.GetPresignedCiphertextUrlAsync(key, TimeSpan.FromMinutes(2));

        // An ABSOLUTE storage address, unlike the download presign above, which swaps to the token door.
        // That is the difference in one line: this one is for a sidecar to fetch the stored bytes, and the
        // stored bytes are ciphertext.
        Assert.True(ciphertext.IsAbsoluteUri, $"the service needs a fetchable address, got {ciphertext}");
        Assert.DoesNotContain("/api/encrypted-content", ciphertext.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_holds_for_the_preview_url()
    {
        // Both presign methods short-circuited on the gate, and a fix applied to one of them would look
        // complete: the download is what a test reaches for, while the PREVIEW is what a user actually
        // opens — so it is the one whose absence would be noticed by everybody and covered by nobody.
        var tenantId = await _factory.SeedTenantNamedAsync($"NoMode{Guid.NewGuid():N}"[..24]);

        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageClient>();

        var key = ObjectKeyBuilder.Build(tenantId, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), ".pdf");
        await storage.PutObjectAsync(key, new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 not really\n")), "application/pdf");
        await storage.SetObjectMetadataAsync(key, new Dictionary<string, string>
        {
            [AtRestMetadata.WrappedDekKey] = Convert.ToBase64String(new byte[256]),
            [AtRestMetadata.KekGenerationKey] = "kek-v1",
        });

        var preview = await storage.GetPresignedPreviewUrlAsync(key, TimeSpan.FromMinutes(5), "x.pdf", "application/pdf");
        Assert.NotNull(preview);
        Assert.False(preview!.IsAbsoluteUri, $"the preview presigned to storage, serving ciphertext: {preview}");
    }
}
