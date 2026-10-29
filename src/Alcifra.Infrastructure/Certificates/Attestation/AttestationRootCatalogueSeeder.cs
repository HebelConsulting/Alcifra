using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Alcifra.Domain.Certificates;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Certificates.Attestation;

/// <summary>
/// Keeps the shipped attestation roots present at every start (#1790, ADR 0963), switched OFF when first added: a fresh
/// installation trusts nothing until its platform administrator decides. Healing, never overruling: a missing entry is
/// added, a changed certificate replaced, and an administrator's name and switch are left as they are.
/// </summary>
/// <remarks>
/// Also the attestation part of the readiness gate: outside Development an installation that trusts the development test
/// root refuses to start, because whoever generated that root can attest any key they like.
/// </remarks>
public static class AttestationRootCatalogueSeeder
{
    public static async Task EnsureAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AttestationRootCatalogueSeeder));

        // A startup step says when it starts and how long it took (ADR 0626), so a stalled start names the step it stalled in.
        var started = System.Diagnostics.Stopwatch.StartNew();
        logger.LogDebug("Attestation root catalogue: checking the shipped roots");
        var entries = AttestationCatalogue.Roots.Select(e => (e.Key, Pem: AttestationCatalogue.Pem(e.Resource))).ToList();
        if (environment.IsDevelopment() && configuration[AttestationCatalogue.TestRootPemKey] is { Length: > 0 } testRoot)
        {
            entries.Add((AttestationCatalogue.TestRootKey, testRoot));
        }

        foreach (var (key, pem) in entries)
        {
            try
            {
                await EnsureEntryAsync(db, key, pem, logger, cancellationToken);
            }
            catch (DbUpdateException e)
            {
                // The other instance added it in the same moment (ADR 0808: both start); its row stands.
                logger.LogDebug(e, "Attestation catalogue entry {CatalogueKey} was added concurrently", key);
                db.ChangeTracker.Clear();
            }
        }

        RefuseTestRootOutsideDevelopment(await db.TrustedAttestationRoots.AsNoTracking().ToListAsync(cancellationToken), environment);
        logger.LogInformation("Attestation root catalogue checked: {Count} shipped root(s) in {Elapsed} ms", entries.Count, started.ElapsedMilliseconds);
    }

    /// <summary>Throws outside Development when the development test root is trusted (enabled).</summary>
    public static void RefuseTestRootOutsideDevelopment(IEnumerable<TrustedAttestationRoot> roots, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() && roots.Any(r => r.CatalogueKey == AttestationCatalogue.TestRootKey && r.Enabled))
        {
            throw new InvalidOperationException(
                $"Refusing to start in the '{environment.EnvironmentName}' environment: the development test attestation root "
                + $"(catalogue key {AttestationCatalogue.TestRootKey}) is trusted, so any key its generator chooses would count as "
                + "hardware-attested. Switch it off (alcifra attestation-roots disable) or run with ASPNETCORE_ENVIRONMENT=Development.");
        }
    }

    private static async Task EnsureEntryAsync(AlcifraDbContext db, string key, string pem, ILogger logger, CancellationToken cancellationToken)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        var thumbprint = AttestationCatalogue.Thumbprint(certificate);
        var row = await db.TrustedAttestationRoots.FirstOrDefaultAsync(r => r.CatalogueKey == key, cancellationToken)
            // An administrator uploaded this very root before it was catalogued: adopt it, keeping their switch.
            ?? await db.TrustedAttestationRoots.FirstOrDefaultAsync(r => r.Thumbprint == thumbprint && r.CatalogueKey == null, cancellationToken);
        if (row is null)
        {
            db.TrustedAttestationRoots.Add(new TrustedAttestationRoot
            {
                Id = Guid.NewGuid(),
                Name = certificate.GetNameInfo(X509NameType.SimpleName, false),
                CertificatePem = certificate.ExportCertificatePem(),
                Thumbprint = thumbprint,
                CatalogueKey = key,
                Enabled = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Attestation root {CatalogueKey} added to the catalogue, switched off", key);
            return;
        }

        if (row.CatalogueKey != key || row.Thumbprint != thumbprint)
        {
            (row.CatalogueKey, row.Thumbprint, row.CertificatePem) = (key, thumbprint, certificate.ExportCertificatePem());
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Attestation root {CatalogueKey} healed to the shipped certificate {Thumbprint}", key, thumbprint);
        }
    }
}
