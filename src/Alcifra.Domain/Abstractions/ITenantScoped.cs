namespace Alcifra.Domain.Abstractions;

/// <summary>
/// Marks an entity as belonging to a single tenant. Every entity implementing this interface
/// automatically gets a global EF Core query filter applied (see AlcifraDbContext), which is
/// the sole tenant-isolation enforcement point (see ADR: Multi-tenancy resolution strategy) —
/// do not additionally filter by TenantId in individual queries/repositories.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
