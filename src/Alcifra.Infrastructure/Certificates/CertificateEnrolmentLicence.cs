using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Certificates;

/// <summary>
/// What a licence changes about the core's certificate enrolment. Enrolment is offered to every tenant (#1970 slice 4,
/// narrowing ADR 0936); while no active module licenses it (<c>LicensesCertificateEnrolment</c>, ABI 1.17), a user holds at
/// most <see cref="UnlicensedPerUserLimit"/> certificates, and with one, the module's policy governs as before.
/// </summary>
public static class CertificateEnrolmentLicence
{
    /// <summary>The most unrevoked certificates one user holds where no active module licenses enrolment.</summary>
    public const int UnlicensedPerUserLimit = 2;

    /// <summary>Whether an active module licenses enrolment for the ambient tenant.</summary>
    public static async Task<bool> IsLicensedAsync(
        AlcifraDbContext dbContext, IReadOnlyList<ModuleLoader.LoadedModule> modules, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var licensing = modules.Where(m => m.Module.LicensesCertificateEnrolment).Select(m => m.Module.ModuleId).ToList();
        return licensing.Count > 0
            && (await ModuleActivationCheck.ActiveIdsAsync(dbContext, licensing, now, cancellationToken)).Count > 0;
    }

    /// <summary>The most certificates one user may hold here, or null where a licensing module's policy governs.</summary>
    public static async Task<int?> PerUserLimitAsync(
        AlcifraDbContext dbContext, IReadOnlyList<ModuleLoader.LoadedModule> modules, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await IsLicensedAsync(dbContext, modules, now, cancellationToken) ? null : UnlicensedPerUserLimit;
}
