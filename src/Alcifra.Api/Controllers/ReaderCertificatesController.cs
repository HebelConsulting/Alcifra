using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The caller's certificate SET, the one the server addresses envelopes to (#1741; ADR 0842): read-only, for a client
/// that seals something to its own user, such as the phone's offline cache (SimplArchiveMobile #5). Answered by the same
/// rule as delivery (<see cref="ReaderCertificateSource"/>): where a module answers, its set is the only source; else the
/// user's own column. The rel is <c>readerCertificates</c> on <c>/me</c>; editing the column stays <c>smimeCertificate</c>.
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
    AlcifraDbContext dbContext,
    ModuleReaderCertificates moduleCertificates,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    public class ReaderCertificatesResource : HypermediaResource
    {
        /// <summary><c>module</c>, <c>own</c> (the user's column) or <c>none</c>.</summary>
        public string Source { get; set; } = "none";

        /// <summary>Why the set is empty: <c>noneEnrolled</c>, <c>licenceLapsed</c> or <c>moduleFailed</c>; null when it is not.</summary>
        public string? Reason { get; set; }

        public List<ReaderCertificateEntry> Certificates { get; set; } = [];
    }

    public class ReaderCertificateEntry
    {
        public string Pem { get; set; } = string.Empty;

        /// <summary>The module's label for it (e.g. the key's name), or the certificate's subject for the user's column.</summary>
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
        var fromModule = await moduleCertificates.ForAsync(userId, cancellationToken);
        var column = fromModule.ModuleSpoke
            ? null
            : await dbContext.Users.Where(u => u.Id == userId).Select(u => u.SmimeCertificatePem).FirstOrDefaultAsync(cancellationToken);
        var resource = Describe(fromModule, column);
        resource.Links = [new Link("self", "/api/me/reader-certificates", "GET")];
        return resource;
    }

    /// <summary>The answer for a module's outcome and the user's column: the same choice delivery makes, with its reason.</summary>
    public static ReaderCertificatesResource Describe(ReaderCertificateAnswer fromModule, string? columnPem)
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

        return columnPem is { Length: > 0 } own
            ? new ReaderCertificatesResource { Source = "own", Certificates = [Entry(own, null)] }
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
