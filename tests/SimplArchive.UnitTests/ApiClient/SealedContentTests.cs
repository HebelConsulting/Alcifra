using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.ApiClient;
using SimplArchive.Localization;

namespace SimplArchive.UnitTests.ApiClient;

// The rules the desktop and the phone apply to a sealed envelope (ADR 0969): only the signed-in user's enrolled keys are
// used, and the state a preview shows is judged against the user's WHOLE enrolled set. Shared, so both apps answer the
// same document the same way.
public sealed class SealedContentTests
{
    [Fact]
    public void Sealed_to_an_enrolled_key_that_is_not_here_names_that_key()
    {
        using var office = NewCertificate();

        var outcome = SealedContent.Judge(RecipientsOf(office), [], Known(("Office key", office)));

        Assert.Equal(new SealedOutcome(SealedContentState.Sealed, "Office key"), outcome);
        Assert.Equal("PvSealedInsertKey", outcome.TitleKey);
        Assert.Null(outcome.SublineKey);
    }

    [Fact]
    public void Sealed_to_two_enrolled_keys_asks_for_a_key_without_naming_one()
    {
        using var office = NewCertificate();
        using var spare = NewCertificate();

        var outcome = SealedContent.Judge(RecipientsOf(office, spare), [], Known(("Office key", office), ("Spare", spare)));

        Assert.Equal(SealedOutcome.NoKey, outcome);
        Assert.Equal("PvSealedNoKey", outcome.TitleKey);
    }

    [Fact]
    public void Sealed_to_none_of_the_enrolled_keys_is_sealed_for_other_keys_with_or_without_a_key_here()
    {
        using var other = NewCertificate();
        using var mine = NewCertificate();

        foreach (var present in new[] { Array.Empty<X509Certificate2>(), [mine] })
        {
            var outcome = SealedContent.Judge(RecipientsOf(other), present, Known(("Office key", mine)));

            Assert.Equal(SealedContentState.SealedForOtherKeys, outcome.State);
            Assert.Equal("PvSealedForOtherKeys", outcome.TitleKey);
            Assert.Equal("PvNoneEnrolledAmongThem", outcome.SublineKey);
        }
    }

    [Fact]
    public void A_present_addressee_not_enrolled_for_the_user_is_refused_as_not_enrolled()
    {
        // florian's key on crypt's machine: an addressee, but crypt's enrolled set does not hold it.
        using var florians = NewCertificate();
        using var crypts = NewCertificate();
        var enrolled = Known(("Crypt", crypts));

        Assert.False(SealedContent.Admits(florians, enrolled));
        Assert.True(SealedContent.Admits(crypts, enrolled));
        Assert.Equal(SealedOutcome.NotEnrolled, SealedContent.Judge(RecipientsOf(florians), [florians], enrolled));
    }

    [Fact]
    public void Unknown_enrolled_keys_admit_no_key_and_say_they_could_not_be_checked()
    {
        using var key = NewCertificate();

        Assert.False(SealedContent.Admits(key, EnrolledKeysAnswer.Unknown));
        Assert.Equal(SealedOutcome.Unknown, SealedContent.Judge(RecipientsOf(key), [key], EnrolledKeysAnswer.Unknown));
    }

    [Fact]
    public async Task Offline_the_keys_last_seen_online_are_used_and_with_none_seen_nothing_is_known()
    {
        using var mine = NewCertificate();
        var memory = new InMemoryEnrolledKeys();
        var online = true;
        var session = new SignedInKeys(_ => online
            ? Task.FromResult<IReadOnlyList<EnrolledKey>?>([EnrolledKey.Of(mine, "Office key")])
            : throw new HttpRequestException("offline"), memory);

        Assert.Equal(EnrolledKeysAnswer.Unknown, await new SignedInKeys(_ => throw new HttpRequestException("offline"), new InMemoryEnrolledKeys()).CurrentAsync());

        var seen = await session.CurrentAsync();
        online = false;
        session.Forget();
        var recalled = await session.CurrentAsync();

        Assert.False(seen.FromMemory);
        Assert.True(recalled.FromMemory);
        Assert.True(SealedContent.Admits(mine, recalled));
    }

    [Fact]
    public void Each_refusal_maps_to_its_state_and_a_real_failure_to_none()
    {
        var other = new SealedOutcome(SealedContentState.SealedForOtherKeys);
        Assert.Equal(other, SealedContent.OutcomeOf(new EnvelopeStaysSealedException("x", other)));
        Assert.Equal(SealedOutcome.NotEnrolled, SealedContent.OutcomeOf(new KeyPromptCancelledException(SealedOutcome.NotEnrolled)));
        Assert.Equal(SealedOutcome.NoKey, SealedContent.OutcomeOf(new OperationCanceledException("The key prompt was cancelled.")));
        Assert.IsAssignableFrom<EnvelopeNotOpenedException>(new EnvelopeStaysSealedException("x", other));   // existing handlers keep working

        // A damaged envelope, a network failure and a timed-out request are errors, said as errors.
        Assert.Null(SealedContent.OutcomeOf(new EnvelopeNotOpenedException("damaged")));
        Assert.Null(SealedContent.OutcomeOf(new HttpRequestException("down")));
        Assert.Null(SealedContent.OutcomeOf(new TaskCanceledException("timed out")));
    }

    [Fact]
    public void The_titles_and_sublines_read_as_the_owner_worded_them()
    {
        var en = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("Sealed for other keys.", Strings.Get("PvSealedForOtherKeys", en));
        Assert.Equal("None of your enrolled keys is among them.", Strings.Get("PvNoneEnrolledAmongThem", en));
        Assert.Equal("This key is not enrolled for you.", Strings.Get("PvKeyNotEnrolledForYou", en));
        Assert.Equal("Sign in as the key's owner, or use a key enrolled for you.", Strings.Get("PvKeyNotEnrolledForYouHint", en));
        Assert.Equal("Für andere Schlüssel versiegelt.", Strings.Get("PvSealedForOtherKeys", System.Globalization.CultureInfo.GetCultureInfo("de")));
        Assert.Equal("Keiner Ihrer eingetragenen Schlüssel gehört dazu.", Strings.Get("PvNoneEnrolledAmongThem", System.Globalization.CultureInfo.GetCultureInfo("de")));
    }

    public static TheoryData<string, string> EveryKeyInEveryLanguage()
    {
        var data = new TheoryData<string, string>();
        string[] keys = ["PvSealedNoKey", "PvSealedInsertKey", "PvSealedForOtherKeys", "PvNoneEnrolledAmongThem", "PvKeyNotEnrolledForYou",
            "PvKeyNotEnrolledForYouHint", "PvEnrolledKeysUnknown", "PvEnrolledKeysUnknownHint", "PvOpenWithKey"];
        foreach (var language in new[] { "de", "it", "es" })
        {
            foreach (var key in keys)
            {
                data.Add(language, key);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryKeyInEveryLanguage))]
    public void Every_sentence_is_translated(string language, string key)
    {
        var english = Strings.Get(key, System.Globalization.CultureInfo.InvariantCulture);

        Assert.NotEqual(key, english);   // the key exists
        Assert.NotEqual(english, Strings.Get(key, System.Globalization.CultureInfo.GetCultureInfo(language)));
    }

    private static EnrolledKeysAnswer Known(params (string Label, X509Certificate2 Certificate)[] keys) =>
        new([.. keys.Select(k => EnrolledKey.Of(k.Certificate, k.Label))]);

    private static IReadOnlyList<(string Issuer, string Serial)> RecipientsOf(params X509Certificate2[] addressees)
    {
        var cms = new EnvelopedCms(new ContentInfo([1, 2, 3]));
        var recipients = new CmsRecipientCollection();
        foreach (var addressee in addressees)
        {
            recipients.Add(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, addressee));
        }

        cms.Encrypt(recipients);
        return EnvelopeRecipients.Of(cms.Encode());
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=reader-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
