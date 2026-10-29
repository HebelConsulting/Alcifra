using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Modules;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Certificates;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace Alcifra.IntegrationTests;

// The Encryption Module's two enrolment settings move into the tenant's columns ONCE (ADR 0936). A move rather than a
// copy, because a copy would run at every start and put the module's old value back over an administrator's change.
public class EnrolmentSettingsAdoptionTests
{
    private sealed class LicensingModule : IIndustryModule
    {
        public bool LicensesCertificateEnrolment => true;
        public string ModuleId => "encryption";
        public string DisplayName => "Encryption";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [];
        public void ConfigureServices(IServiceCollection services) { }
    }

    private static (ServiceProvider Root, Guid TenantId) Build(SqliteConnection connection, bool licensing)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddScoped<CurrentTenantAccessor>();
        services.AddScoped<ICurrentTenantAccessor>(sp => sp.GetRequiredService<CurrentTenantAccessor>());
        services.AddScoped(sp => new AlcifraDbContext(
            new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options,
            sp.GetRequiredService<ICurrentTenantAccessor>()));
        services.AddSingleton<IReadOnlyList<ModuleLoader.LoadedModule>>(
            licensing ? [new ModuleLoader.LoadedModule(new LicensingModule(), "test://encryption")] : []);
        var root = services.BuildServiceProvider();

        var tenantId = Guid.NewGuid();
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        return (root, tenantId);
    }

    private static void Setting(ServiceProvider root, Guid tenantId, string key, string value)
    {
        using var scope = root.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        db.ModuleSettingValues.Add(new ModuleSettingValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ModuleId = "encryption",
            Key = key,
            Value = value,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static (Tenant Tenant, int ModuleRows) Read(ServiceProvider root, Guid tenantId)
    {
        using var scope = root.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        return (db.Tenants.AsNoTracking().Single(t => t.Id == tenantId), db.ModuleSettingValues.Count(v => v.ModuleId == "encryption"));
    }

    [Fact]
    public async Task The_module_values_become_the_tenant_columns_and_leave_the_module()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (root, tenantId) = Build(connection, licensing: true);
        var parent = Guid.NewGuid();
        Setting(root, tenantId, EnrolmentSettingsAdoption.SelfServiceKey, "true");
        Setting(root, tenantId, EnrolmentSettingsAdoption.ParentFolderKey, parent.ToString());

        await EnrolmentSettingsAdoption.AdoptEverywhereAsync(root);

        var (tenant, moduleRows) = Read(root, tenantId);
        Assert.True(tenant.CertificateSelfServiceEnabled);
        Assert.Equal(parent, tenant.CertificateParentFolderId);
        Assert.Equal(0, moduleRows);
    }

    [Fact]
    public async Task A_second_start_does_not_undo_an_administrators_later_change()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (root, tenantId) = Build(connection, licensing: true);
        Setting(root, tenantId, EnrolmentSettingsAdoption.SelfServiceKey, "true");
        await EnrolmentSettingsAdoption.AdoptEverywhereAsync(root);

        using (var scope = root.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            (await db.Tenants.SingleAsync(t => t.Id == tenantId)).CertificateSelfServiceEnabled = false;
            await db.SaveChangesAsync();
        }

        await EnrolmentSettingsAdoption.AdoptEverywhereAsync(root);

        Assert.False(Read(root, tenantId).Tenant.CertificateSelfServiceEnabled);
    }

    [Fact]
    public async Task Nothing_moves_while_no_module_licenses_enrolment()
    {
        // An Encryption Module older than ABI 1.17 still reads these settings itself; moving them would switch its
        // self-service off under it.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (root, tenantId) = Build(connection, licensing: false);
        Setting(root, tenantId, EnrolmentSettingsAdoption.SelfServiceKey, "true");

        await EnrolmentSettingsAdoption.AdoptEverywhereAsync(root);

        var (tenant, moduleRows) = Read(root, tenantId);
        Assert.False(tenant.CertificateSelfServiceEnabled);
        Assert.Equal(1, moduleRows);
    }
}
