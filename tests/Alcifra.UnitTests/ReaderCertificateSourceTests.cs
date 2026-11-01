using SimplArchive.ModuleAbi;

namespace Alcifra.UnitTests;

/// <summary>
/// Where an IMAP reader's certificates come from (ADR 0855), and the distinction that is easy to lose.
/// </summary>
/// <remarks>
/// Written because the first draft of that change lost it. Where a module answers it is the ONLY source (ADR 0842): an
/// empty answer, a lapsed licence and a failed ask all close every other source, or a certificate the module REVOKED
/// goes on opening mail — the union ADR 0842 forbids. Since #1970 slice 4 the other source is the core's registry, which
/// answers only where no module speaks; the user's own certificate column is no longer one.
/// </remarks>
public class ReaderCertificateSourceTests
{
    private static IReadOnlyList<ReaderCertificate> Certificates(params string[] pems) =>
        [.. pems.Select(pem => new ReaderCertificate(pem, "device", DateTimeOffset.MaxValue))];

    [Fact]
    public void A_modules_answer_is_used_and_no_other_source_may_be_consulted()
    {
        var source = Alcifra.Infrastructure.Modules.ReaderCertificateSource.Resolve(
            Alcifra.Infrastructure.Modules.ReaderCertificateAnswer.Answered(Certificates("card-pem", "laptop-pem")));

        Assert.Equal(["card-pem", "laptop-pem"], source.Pems);
        Assert.True(source.AnsweredByModule);
        Assert.True(source.Envelopes);
    }

    [Fact] // THE case the first draft got wrong
    public void A_module_answering_NONE_still_closes_every_other_source()
    {
        var source = Alcifra.Infrastructure.Modules.ReaderCertificateSource.Resolve(
            Alcifra.Infrastructure.Modules.ReaderCertificateAnswer.Answered([]));

        Assert.Empty(source.Pems);
        Assert.False(source.Envelopes);              // nothing to envelope to...
        Assert.True(source.AnsweredByModule);         // ...and the module has spoken, so nothing else is asked
    }

    [Fact]
    public void With_no_module_the_registry_answers()
    {
        var source = Alcifra.Infrastructure.Modules.ReaderCertificateSource.Resolve(
            Alcifra.Infrastructure.Modules.ReaderCertificateAnswer.FromRegistry(Certificates("token-pem")));

        Assert.Equal(["token-pem"], source.Pems);
        Assert.False(source.AnsweredByModule);
        Assert.True(source.Envelopes);
    }

    [Fact]
    public void With_no_module_and_nothing_enrolled_there_is_nothing_to_envelope_to()
    {
        // Nothing to address, so the FETCH path serves plaintext (or refuses a device promised sealing).
        var source = Alcifra.Infrastructure.Modules.ReaderCertificateSource.Resolve(
            Alcifra.Infrastructure.Modules.ReaderCertificateAnswer.FromRegistry([]));

        Assert.Empty(source.Pems);
        Assert.False(source.Envelopes);
        Assert.False(source.AnsweredByModule);
    }

    [Theory]
    [InlineData(Alcifra.Infrastructure.Modules.ReaderCertificateOutcome.LicenceLapsed)]
    [InlineData(Alcifra.Infrastructure.Modules.ReaderCertificateOutcome.AskFailed)]
    public void A_lapsed_licence_or_a_failed_ask_also_closes_every_other_source(
        Alcifra.Infrastructure.Modules.ReaderCertificateOutcome outcome)
    {
        // ADR 0859's addition to the same rule: neither of these said "this reader has none", so nothing is addressed.
        var source = Alcifra.Infrastructure.Modules.ReaderCertificateSource.Resolve(
            new Alcifra.Infrastructure.Modules.ReaderCertificateAnswer(outcome, []));

        Assert.Empty(source.Pems);
        Assert.False(source.Envelopes);
        Assert.True(source.AnsweredByModule);
    }
}
