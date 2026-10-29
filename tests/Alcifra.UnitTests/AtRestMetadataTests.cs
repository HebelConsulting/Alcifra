using Alcifra.Infrastructure.Storage;

namespace Alcifra.UnitTests;

// ADR 1003: the at-rest keys are written as alcifra-* and read as either name for ever, because a WORM-locked object
// written before the rename can never be rewritten.
public sealed class AtRestMetadataTests
{
    [Fact]
    public void The_keys_before_the_rename_are_read()
    {
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sa-wrapped-dek"] = "w",
            ["sa-kek-generation"] = "kek-v1",
            ["sa-kek-thumbprint"] = "t",
        };

        Assert.True(AtRestMetadata.IsEncrypted(before));
        Assert.Equal(("w", "kek-v1", "t"), (AtRestMetadata.WrappedDek(before), AtRestMetadata.Generation(before), AtRestMetadata.Thumbprint(before)));
    }

    [Fact]
    public void Plaintext_carries_neither_and_the_current_key_wins_over_the_old_one()
    {
        Assert.False(AtRestMetadata.IsEncrypted(new Dictionary<string, string>()));

        var both = new Dictionary<string, string> { ["alcifra-kek-generation"] = "kek-v2", ["sa-kek-generation"] = "kek-v1" };
        Assert.Equal("kek-v2", AtRestMetadata.Generation(both));
    }

    [Fact]
    public void A_stamp_writes_the_current_keys_and_removes_the_old_ones_and_a_stale_thumbprint()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sa-wrapped-dek"] = "old",
            ["sa-kek-generation"] = "kek-v1",
            ["sa-kek-thumbprint"] = "old-key",
            ["alcifra-kek-thumbprint"] = "stale",
            ["other"] = "kept",
        };

        AtRestMetadata.Stamp(metadata, "new", "kek-v2", thumbprint: null);

        Assert.Equal(
            new Dictionary<string, string> { ["alcifra-wrapped-dek"] = "new", ["alcifra-kek-generation"] = "kek-v2", ["other"] = "kept" }.OrderBy(p => p.Key),
            metadata.OrderBy(p => p.Key));
    }
}
