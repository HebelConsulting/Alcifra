using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

/// <summary>
/// A scope factory for a host carrying NO modules — what `EmailNotificationDispatcher` needs to ask the
/// reader-certificate question at all (ADR 0856), answering "nothing enrols certificates here".
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher takes <see cref="IServiceScopeFactory"/> as a REQUIRED dependency, and these tests pay
/// for that by supplying one. Making it optional was the alternative and is the wrong direction: a
/// production wiring that forgot it would then silently stop asking the module, and the failure of that
/// omission is PLAINTEXT notification mail on a tenant that believes its notifications are sealed — a
/// silent omission, which is exactly what constructor injection exists to prevent (ADR 0730).
/// </para>
/// <para>
/// No module is registered, so <c>ModuleReaderCertificates.ForAsync</c> answers from the core's registry (#1970 slice 4).
/// The registry is tenant-scoped, so a test that reads it passes the accessor its context filters by: the dispatcher sets
/// the tenant on the scope's accessor, and a fresh one would leave the context's filter matching nothing.
/// </para>
/// </remarks>
internal sealed class NoModuleScopeFactory(AlcifraDbContext dbContext, CurrentTenantAccessor? tenant = null)
    : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    private readonly CurrentTenantAccessor _tenant = tenant ?? new CurrentTenantAccessor();

    public IServiceScope CreateScope() => this;

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType) => serviceType switch
    {
        _ when serviceType == typeof(ICurrentTenantAccessor) => _tenant,
        _ when serviceType == typeof(AlcifraDbContext) => dbContext,
        _ when serviceType == typeof(ModuleReaderCertificates) => new ModuleReaderCertificates(
            dbContext, this, NullLogger<ModuleReaderCertificates>.Instance),

        // Everything else is genuinely absent, which is the point: a host with no modules. Returning null
        // is how ModuleReaderCertificates learns there is no loaded-module list to consult.
        _ => null,
    };

    public void Dispose()
    {
        // The DbContext belongs to the test, not to this scope.
    }
}
