namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// Which certificates a reader is addressed by, and WHERE the answer came from (ADRs 0855/0856).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two facts, not one, and the second is the one that is easy to lose.</b>
/// <c>ModuleReaderCertificates.ForAsync</c> returns null when no active module answers the question and an
/// EMPTY LIST when one did and this reader has none — a distinction ADR 0842 makes load-bearing, because
/// where a module answers it is the ONLY source. Collapsing the two into "no certificates" made an empty
/// answer indistinguishable from silence, and the fetch then tried the encryption service's registry next:
/// the module said "this reader holds none", the registry said "here is one", and a certificate the module
/// had REVOKED went on opening mail. The registry is retired (ADR 0890), but the column is a second source
/// too, and the distinction decided whether it might be asked until the column retired (#1970 slice 4).
/// </para>
/// <para>
/// The first draft of this change did exactly that. It is a record rather than two loose fields so the two
/// travel together and a caller cannot read one without the other.
/// </para>
/// <para>
/// <b>It lives in Infrastructure because TWO callers need the same rule</b> (ADR 0856): the IMAP session and
/// the notification dispatcher. It began in <c>Api.Imap</c> with one caller, and the dispatcher is in
/// Infrastructure — which cannot reference Api — so a second copy was the alternative, and a second copy of a
/// precedence rule is how the two come to disagree about whether a revoked certificate still counts.
/// </para>
/// </remarks>
/// <param name="Pems">The certificates to envelope to — possibly none.</param>
/// <param name="AnsweredByModule">
/// True when an active module answered. No other source was consulted then, whatever <paramref name="Pems"/>
/// holds.
/// </param>
public sealed record ReaderCertificateSource(IReadOnlyList<string> Pems, bool AnsweredByModule)
{
    /// <summary>Nothing answered, and no other source has been consulted yet.</summary>
    public static readonly ReaderCertificateSource None = new([], AnsweredByModule: false);

    /// <summary>True when this session envelopes in-process to the certificates above.</summary>
    public bool Envelopes => Pems.Count > 0;

    /// <summary>
    /// The answer as it came: a module's where one speaks, else the core's registry (#1970 slice 4). The user's own
    /// certificate column (#1332) is no longer a source.
    /// </summary>
    /// <param name="answer">What <c>ModuleReaderCertificates.ForAsync</c> returned.</param>
    public static ReaderCertificateSource Resolve(ReaderCertificateAnswer answer) =>
        new([.. answer.Certificates.Select(certificate => certificate.CertificatePem)], answer.ModuleSpoke);
}
