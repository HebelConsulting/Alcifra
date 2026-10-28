using SimplArchive.Api.PasswordStores;

namespace SimplArchive.UnitTests.PasswordStore;

// ADR 0982: who is offered a new password entry. A person must be one of the store's readers AND hold a certificate the
// entry is sealed to; a service account (alcifra's seeding, ADR 0966) needs only that some reader holds one.
public sealed class NewEntryUnavailableReasonsTests
{
    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Seeder = Guid.NewGuid();

    private static PasswordStoreReader Reader(Guid id, int certificates) =>
        new(id, id.ToString(), [.. Enumerable.Range(0, certificates).Select(i => $"pem-{id}-{i}")]);

    [Fact]
    public void A_reader_holding_a_certificate_is_offered_entries() =>
        Assert.Null(NewEntryUnavailableReasons.Of(Anna, null, [Reader(Anna, 1), Reader(Bob, 0)]));

    [Fact]
    public void A_reader_without_a_certificate_must_enrol_even_when_other_readers_hold_one() =>
        Assert.Equal(NewEntryUnavailableReasons.EnrolmentRequired, NewEntryUnavailableReasons.Of(Bob, null, [Reader(Anna, 2), Reader(Bob, 0)]));

    [Fact]
    public void A_person_who_is_no_reader_is_told_so_whatever_keys_they_hold() =>
        Assert.Equal(NewEntryUnavailableReasons.NotAReader, NewEntryUnavailableReasons.Of(Admin, null, [Reader(Anna, 1)]));

    [Fact]
    public void A_caller_who_is_neither_a_person_nor_a_service_account_is_no_reader() =>
        Assert.Equal(NewEntryUnavailableReasons.NotAReader, NewEntryUnavailableReasons.Of(null, null, [Reader(Anna, 1)]));

    [Fact]
    public void A_service_account_seals_to_the_readers_while_one_of_them_holds_a_certificate()
    {
        Assert.Null(NewEntryUnavailableReasons.Of(null, Seeder, [Reader(Anna, 0), Reader(Bob, 1)]));
        Assert.Equal(NewEntryUnavailableReasons.NoReaderCertificates, NewEntryUnavailableReasons.Of(null, Seeder, [Reader(Anna, 0)]));
        Assert.Equal(NewEntryUnavailableReasons.NoReaderCertificates, NewEntryUnavailableReasons.Of(null, Seeder, []));
    }
}
