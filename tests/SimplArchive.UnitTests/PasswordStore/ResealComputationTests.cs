using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.SealedArchive;
using static SimplArchive.UnitTests.PasswordStore.PasswordEntryFormatTests;

namespace SimplArchive.UnitTests.PasswordStore;

// "Needs re-seal" is COMPUTED (ADR 0964): an envelope's recipients, read without a key, against the readers' current
// certificates. Both directions count: a reader added who cannot open it, and a reader removed who still could.
public sealed class ResealComputationTests
{
    private static readonly PasswordEntryContent Content = PasswordEntryContent.Of("pw", "anna");

    [Fact]
    public void An_entry_sealed_to_exactly_the_readers_needs_nothing()
    {
        using var a = Rsa("a");
        using var b = Ec("b");
        var recipients = CmsRecipients.Of(PasswordStoreClient.Seal(Content, [Public(a), Public(b)]));

        Assert.Equal(2, recipients.Count);
        Assert.True(CmsRecipients.AddressedToExactly(recipients, [Public(b), Public(a)]));
    }

    [Fact]
    public void A_reader_added_since_makes_it_need_a_re_seal()
    {
        using var a = Rsa("a");
        using var added = Rsa("added");
        var recipients = CmsRecipients.Of(PasswordStoreClient.Seal(Content, [Public(a)]));

        Assert.False(CmsRecipients.AddressedToExactly(recipients, [Public(a), Public(added)]));
    }

    [Fact]
    public void A_reader_removed_since_makes_it_need_a_re_seal()
    {
        using var a = Rsa("a");
        using var removed = Ec("removed");
        var recipients = CmsRecipients.Of(PasswordStoreClient.Seal(Content, [Public(a), Public(removed)]));

        Assert.False(CmsRecipients.AddressedToExactly(recipients, [Public(a)]));
        Assert.Contains(recipients, r => r.Is(Public(removed)));
    }

    [Fact]
    public void Bytes_that_are_no_envelope_name_no_recipients() =>
        Assert.Empty(CmsRecipients.Of("not an envelope"u8.ToArray()));
}
