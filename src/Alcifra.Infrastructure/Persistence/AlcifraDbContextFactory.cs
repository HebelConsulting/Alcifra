using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef migrations add`/`dotnet ef database update` construct AlcifraDbContext without
/// a running host (no DI container, no real ICurrentTenantAccessor). The connection string here is only
/// used to generate/apply migrations at design time — never at runtime, where AddInfrastructure wires the
/// real one from configuration.
/// </summary>
public class AlcifraDbContextFactory : IDesignTimeDbContextFactory<AlcifraDbContext>
{
    public AlcifraDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AlcifraDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=alcifra;Username=postgres;Password=postgres");

        return new AlcifraDbContext(optionsBuilder.Options, new DesignTimeCurrentTenantAccessor());
    }

    private sealed class DesignTimeCurrentTenantAccessor : Application.Abstractions.ICurrentTenantAccessor
    {
        public Guid? TenantId => null;
    }
}
