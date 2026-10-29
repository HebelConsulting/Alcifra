using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Certificates;

/// <summary>
/// Whether the core offers certificate enrolment to the ambient tenant (ADR 0936): only while a module that licenses it
/// (<c>LicensesCertificateEnrolment</c>, ABI 1.17) is active there. Otherwise the surface does not exist, exactly as an
/// inactive module's routes do not (ADR 0740).
/// </summary>
public static class CertificateEnrolmentLicence
{
    public static async Task<bool> IsOfferedAsync(
        AlcifraDbContext dbContext, IReadOnlyList<ModuleLoader.LoadedModule> modules, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var licensing = modules.Where(m => m.Module.LicensesCertificateEnrolment).Select(m => m.Module.ModuleId).ToList();
        return licensing.Count > 0
            && (await ModuleActivationCheck.ActiveIdsAsync(dbContext, licensing, now, cancellationToken)).Count > 0;
    }
}
