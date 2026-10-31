using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Env-driven seed of the MANUAL tenant (#1787): the archive of a fictional Kristall Holding AG that the user
/// manual's automated screenshots are taken against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tenant of its own.</b> The demo tenant is a showcase visitors change all day; a screenshot run needs
/// an archive that looks the same every time it is captured, reset nightly with the rest of the kiosk. The demo
/// tenant files the same company's archive (#1788) through the same builder, <see cref="KristallSamples"/>, and
/// adds its showcase extras on top; this tenant stays exactly the builder's archive.
/// </para>
/// <para>
/// Same shape as <see cref="CryptoDemoSeeder"/>: a no-op unless <c>ManualDemo:*</c> is configured, a no-op
/// when the tenant exists, deterministic ids (<see cref="DemoId"/>, #781).
/// </para>
/// </remarks>
public static class ManualDemoSeeder
{
    public static async Task SeedIfConfiguredAsync(IServiceProvider services, IConfiguration configuration)
    {
        var tenantName = configuration["ManualDemo:Tenant:Name"];
        var adminEmail = configuration["ManualDemo:Administrator:Email"];
        var password = configuration["ManualDemo:Password"];

        if (string.IsNullOrWhiteSpace(tenantName)
            || string.IsNullOrWhiteSpace(adminEmail)
            || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var dbContext = services.GetRequiredService<AlcifraDbContext>();

        // IgnoreQueryFilters: an earlier seeder may have left its own tenant ambient, which would make this
        // existence check a lie.
        if (await dbContext.Tenants.IgnoreQueryFilters()
                .AnyAsync(t => t.Name == tenantName && t.Status == TenantStatus.Active))
        {
            return;
        }

        // The demo's clock, so a capture run that pins Demo:Clock gets the same archive every time.
        var now = services.GetRequiredKeyedService<TimeProvider>("demo-clock").GetUtcNow();

        var tenantId = DemoId.Root(tenantName);
        var provisioned = await services.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(
            tenantName,
            adminEmail,
            configuration["ManualDemo:Administrator:DisplayName"] ?? "Petra Keller",
            configuration["ManualDemo:RepositoryName"] ?? "Kristall Holding AG",
            password,
            idFor: slug => slug == "tenant" ? tenantId : DemoId.For(tenantId, slug),
            createdAt: now);

        services.GetRequiredService<CurrentTenantAccessor>().TenantId = provisioned.TenantId;

        await KristallSamples.SeedAsync(services, provisioned, now, password, new KristallSamples.KristallOptions(
            FolderSlugPrefix: "manual/",
            LinkTokenSeed: "simplarchive-manual-seewelle-service-agreement-v1"));
    }
}
