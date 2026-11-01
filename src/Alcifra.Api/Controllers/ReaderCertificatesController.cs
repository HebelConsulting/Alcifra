using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Modules;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The caller's certificate SET, the one the server addresses envelopes to (#1741; ADR 0842): read-only, for a client
/// that seals something to its own user, such as the phone's offline cache (SimplArchiveMobile #5). Answered by the same
/// rule as delivery (<see cref="ReaderCertificateSource"/>): where a module answers, its set is the only source; else the
/// core's registry (#1970 slice 4). The rel is <c>readerCertificates</c> on <c>/me</c>; enrolling is <c>certificates</c>.
/// </summary>
/// <remarks>
/// An empty set says WHY (owner decision 2026-10-13), because ADR 0842 keeps the causes apart and they have different
/// fixes: none enrolled (the user enrols a key), the licence lapsed or the module failed (an administrator acts).
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/reader-certificates")]
[Authorize]
public class ReaderCertificatesController(
    ModuleReaderCertificates moduleCertificates,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    public class ReaderCertificatesResource : HypermediaResource
    {
        /// <summary><c>module</c>, <c>registry</c> (the core's, where no module speaks; #1970 slice 4) or <c>none</c>.</summary>
        public string Source { get; set; } = "none";

        /// <summary>Why the set is empty: <c>noneEnrolled</c>, <c>licenceLapsed</c> or <c>moduleFailed</c>; null when it is not.</summary>
        public string? Reason { get; set; }

        public List<ReaderCertificateEntry> Certificates { get; set; } = [];
    }

    public class ReaderCertificateEntry
    {
        public string Pem { get; set; } = string.Empty;

        /// <summary>The enrolment's label for it (e.g. the key's name), or the certificate's subject where it has none.</summary>
        public string Label { get; set; } = string.Empty;

        public DateTimeOffset NotAfter { get; set; }

        /// <summary>SHA-256 of the certificate (DER), lowercase hex: tells a client which of its keys this is.</summary>
        public string ThumbprintSha256 { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId ? Ok(await ReadAsync(userId, cancellationToken)) : Forbid();

    [HttpHead]
    public IActionResult Head() => currentUser.UserId is null ? Forbid() : NoContent();

    private async Task<ReaderCertificatesResource> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var resource = Describe(await moduleCertificates.ForAsync(userId, cancellationToken));
        resource.Links = [new Link("self", "/api/me/reader-certificates", "GET")];
        return resource;
    }

    /// <summary>The answer as delivery reads it, with its reason.</summary>
    public static ReaderCertificatesResource Describe(ReaderCertificateAnswer fromModule)
    {
        if (fromModule.ModuleSpoke)
        {
            return new ReaderCertificatesResource
            {
                Source = "module",
                Reason = fromModule.Outcome switch
                {
                    ReaderCertificateOutcome.LicenceLapsed => "licenceLapsed",
                    ReaderCertificateOutcome.AskFailed => "moduleFailed",
                    _ when fromModule.Certificates.Count == 0 => "noneEnrolled",
                    _ => null,
                },
                Certificates = [.. fromModule.Certificates.Select(c => Entry(c.CertificatePem, c.Label))],
            };
        }

        return fromModule.Certificates.Count > 0
            ? new ReaderCertificatesResource { Source = "registry", Certificates = [.. fromModule.Certificates.Select(c => Entry(c.CertificatePem, c.Label))] }
            : new ReaderCertificatesResource { Source = "none", Reason = "noneEnrolled" };
    }

    private static ReaderCertificateEntry Entry(string pem, string? label)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        return new ReaderCertificateEntry
        {
            Pem = pem,
            Label = label is { Length: > 0 } ? label : certificate.GetNameInfo(X509NameType.SimpleName, false),
            NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            ThumbprintSha256 = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)),
        };
    }
}
