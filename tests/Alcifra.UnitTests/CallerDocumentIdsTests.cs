using Alcifra.Api.Documents;
using Alcifra.Api.Provisioning;

namespace Alcifra.UnitTests;

// A caller's slug is an IDENTITY (#2501), so the derivation is a promise across releases exactly as the demo seeder's
// is (#781): the kiosk's tube seed derives its ids from slugs, and a shared video address survives the nightly reset
// only while the same slug keeps producing the same GUID. The goldens below are computed independently with a
// reference RFC 4122 v5 implementation (uuid.uuid5 with the caller namespace), so they also pin the namespace.
//
// If a change fails these tests, it changes every slug-derived id: the kiosk's seeded channels, playlists, videos and
// thumbnails get new addresses. That is a deliberate migration, never a cleanup.
public class CallerDocumentIdsTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-3333-4444-8555-666666666666");
    private static readonly Guid OtherTenant = Guid.Parse("33333333-4444-4555-8666-777777777777");

    [Theory]
    [InlineData("tube/channel/alcifra", "4c2eef88-5b22-5257-a413-dca12a669b72")]
    [InlineData("tube/playlist/english", "5640e733-23f9-556c-b3b9-c6612fc0da6d")]
    public void Slugs_produce_their_golden_ids(string slug, string expected)
        => Assert.Equal(Guid.Parse(expected), CallerDocumentIds.For(Tenant, slug));

    [Fact]
    public void The_tenant_is_part_of_the_identity()
        => Assert.Equal(
            Guid.Parse("471b6f82-a857-5ab7-a1d2-0a7fab3fe4c7"),
            CallerDocumentIds.For(OtherTenant, "tube/channel/alcifra"));

    [Fact]
    public void A_caller_slug_never_lands_on_a_demo_seeded_id()
        => Assert.NotEqual(DemoId.For(Tenant, "tube/channel/alcifra"), CallerDocumentIds.For(Tenant, "tube/channel/alcifra"));

    [Fact]
    public void The_ids_carry_the_version_5_marker()
    {
        var id = CallerDocumentIds.For(Tenant, "tube/channel/alcifra").ToString("D");
        Assert.Equal('5', id[14]);
    }
}
