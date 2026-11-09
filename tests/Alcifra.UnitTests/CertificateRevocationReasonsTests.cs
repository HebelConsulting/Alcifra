using System.Globalization;
using Alcifra.Presentation;

namespace Alcifra.UnitTests;

/// <summary>
/// The reasons a key is revoked for (#2278): what is stored for a choice, and that every client can name each choice in
/// every language.
/// </summary>
public sealed class CertificateRevocationReasonsTests
{
    [Theory]
    [InlineData("lost", null, "lost")]
    [InlineData("left", "ignored", "left")]
    [InlineData("other", "  Card swapped at the front desk ", "Card swapped at the front desk")]
    public void A_choice_stores_its_code_or_the_words_given_for_other(string choice, string? text, string expected) =>
        Assert.Equal(expected, CertificateRevocationReasons.Stored(choice, text));

    [Theory]
    [InlineData(null, null)]
    [InlineData("misplaced", null)]
    [InlineData("other", null)]
    [InlineData("other", " ")]
    public void An_incomplete_choice_stores_nothing(string? choice, string? text) =>
        Assert.Null(CertificateRevocationReasons.Stored(choice, text));

    [Fact]
    public void Words_longer_than_the_limit_are_not_stored() =>
        Assert.Null(CertificateRevocationReasons.Stored("other", new string('x', CertificateRevocationReasons.OtherTextLimit + 1)));

    [Fact]
    public void Words_given_for_other_read_as_written_and_a_code_reads_as_its_choice()
    {
        Assert.Null(CertificateRevocationReasons.StoredKey("Card swapped at the front desk"));
        Assert.Equal("RevokeReasonCompromised", CertificateRevocationReasons.StoredKey("compromised"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("es")]
    public void Every_choice_has_its_words_in_every_language(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        foreach (var choice in CertificateRevocationReasons.Choices)
        {
            var key = CertificateRevocationReasons.ChoiceKey(choice);
            Assert.NotEqual(key, Alcifra.Localization.Strings.Get(key, culture));
        }
    }
}
