using SimplArchive.ApiClient.KeyTest;
using SimplArchive.Localization;

namespace SimplArchive.UnitTests.ApiClient;

// The verdict both clients show above the report (owner 2026-10-22, ADR 0962): one headline, one plain reason, read
// from the failing step's kind and its exact code — the desktop's PKCS#11 return values and the phone's status words.
public sealed class KeyTestVerdictTextTests
{
    [Theory]
    [InlineData(KeyTestStepKind.FindLibrary, "NO_LIBRARY", "KtWhyNoLibrary")]
    [InlineData(KeyTestStepKind.ListReaders, "NO_READER", "KtWhyNoKey")]
    [InlineData(KeyTestStepKind.ReadKey, "NO_KEY", "KtWhyNoKey")]
    [InlineData(KeyTestStepKind.Connect, "TimeoutException", "KtWhyNoKey")]
    [InlineData(KeyTestStepKind.SelectPiv, "SW 6D00", "KtWhyNoPiv")]
    [InlineData(KeyTestStepKind.ChooseCertificate, "NO_CERTIFICATE", "KtWhyNoCertificate")]
    [InlineData(KeyTestStepKind.ChooseCertificate, "SW 6A82 in GET DATA 5FC10B (no certificate in slot 9D)", "KtWhyNoCertificate")]
    [InlineData(KeyTestStepKind.Seal, "CryptographicException", "KtWhyCannotSeal")]
    [InlineData(KeyTestStepKind.Unlock, "CKR_PIN_INCORRECT (0x000000A0) in C_Login", "KtWhyWrongPin")]
    [InlineData(KeyTestStepKind.Unlock, "CKR_PIN_LOCKED (0x000000A4) in C_Login", "KtWhyPinBlocked")]
    [InlineData(KeyTestStepKind.Unlock, "SW 6983 (PIN blocked)", "KtWhyPinBlocked")]
    [InlineData(KeyTestStepKind.Unlock, "SW 63C0 (wrong PIN, 0 left)", "KtWhyPinBlocked")]
    [InlineData(KeyTestStepKind.Unlock, "KEY_NOT_IN_READER", "KtWhyNoKey")]
    [InlineData(KeyTestStepKind.Unlock, "CKR_DEVICE_ERROR (0x00000030) in C_OpenSession", "KtWhyNotUnlocked")]
    [InlineData(KeyTestStepKind.Open, "CKR_FUNCTION_FAILED (0x00000006) in C_Decrypt", "KtWhyNotOpened")]
    [InlineData(KeyTestStepKind.Compare, "OPENED_DIFFERENT", "KtWhyNotOpened")]
    public void A_failed_step_is_explained_in_plain_words(KeyTestStepKind kind, string code, string reasonKey)
    {
        var line = KeyTestVerdictText.Of(Failed(kind, code));

        Assert.Equal(KeyTestVerdict.Failed, line.Verdict);
        Assert.Equal(Strings.Get("KtDoesNotWork"), line.Headline);
        Assert.Equal(Strings.Get(reasonKey), line.Reason);
    }

    [Fact]
    public void A_wrong_PIN_on_a_PIV_key_says_how_many_tries_are_left()
    {
        var line = KeyTestVerdictText.Of(Failed(KeyTestStepKind.Unlock, "SW 63C2 (wrong PIN, 2 left)"));

        Assert.Equal(string.Format(Strings.Get("KtWhyWrongPinLeft"), 2), line.Reason);
        Assert.Equal(2, KeyTestVerdictText.PinTriesLeft("SW 63C2 (wrong PIN, 2 left)"));
        Assert.Null(KeyTestVerdictText.PinTriesLeft("CKR_PIN_INCORRECT (0x000000A0) in C_Login"));
        Assert.Null(KeyTestVerdictText.PinTriesLeft(null));
    }

    [Fact]
    public void The_reason_is_the_FIRST_failure_not_a_later_one()
    {
        var builder = new KeyTestReportBuilder();
        builder.Step(KeyTestStepKind.ChooseCertificate, KeyTestOutcome.Failed, "none", "Key A", "NO_CERTIFICATE");
        builder.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Failed, "wrong", "Key B", "CKR_PIN_INCORRECT (0x000000A0) in C_Login");

        Assert.Equal(Strings.Get("KtWhyNoCertificate"), KeyTestVerdictText.Of(builder.Build()).Reason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, "MobKtEnrolled")]
    [InlineData(false, "MobKtNotEnrolled")]
    public void A_key_that_works_says_so_and_signed_in_whether_it_is_enrolled(bool? enrolled, string? reasonKey)
    {
        var builder = new KeyTestReportBuilder();
        builder.Step(KeyTestStepKind.Compare, KeyTestOutcome.Passed, "same");
        if (enrolled is { } isEnrolled)
        {
            builder.Archive(new KeyTestArchiveSide("https://a", "1", "module", null,
                [new KeyTestEnrolment("ab", isEnrolled)], null, null, null, []));
        }

        var line = KeyTestVerdictText.Of(builder.Build());

        Assert.Equal(KeyTestVerdict.Works, line.Verdict);
        Assert.Equal(Strings.Get("KtWorks"), line.Headline);
        Assert.Equal(reasonKey is null ? null : Strings.Get(reasonKey), line.Reason);
    }

    [Fact]
    public void A_declined_PIN_is_unfinished_neither_working_nor_failing()
    {
        var builder = new KeyTestReportBuilder();
        builder.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Skipped, "no PIN");

        var line = KeyTestVerdictText.Of(builder.Build());

        Assert.Equal(KeyTestVerdict.NotCompleted, line.Verdict);
        Assert.Equal(Strings.Get("KtNotCompleted"), line.Headline);
        Assert.Null(line.Reason);
    }

    [Fact]
    public void Every_step_kind_has_a_reason()
    {
        foreach (var kind in Enum.GetValues<KeyTestStepKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(KeyTestVerdictText.ReasonOf(new KeyTestStep(kind, KeyTestOutcome.Failed, "x"))));
        }
    }

    private static KeyTestReport Failed(KeyTestStepKind kind, string code)
    {
        var builder = new KeyTestReportBuilder();
        builder.Step(kind, KeyTestOutcome.Failed, "failed", "Key", code);
        return builder.Build();
    }
}
