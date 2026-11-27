using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Alcifra.EndToEndTests;

// ADR 1113 on a real Postgres: the per-document advisory lock makes a second allocation WAIT for the first — the one
// property an index cannot give, proven deterministically rather than hoped for from racing HTTP calls. Self-contained
// (its own throwaway Postgres container), like ProtocolDeviceMigrationTests. Needs Docker.
[Trait("Area", "e2e-1")]
public class DocumentVersionNumberLockTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("alcifra")
            .Build();
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private AlcifraDbContext Context(Guid tenantId) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    [Fact]
    public async Task A_second_allocation_waits_for_the_first_and_sees_its_number()
    {
        var at = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        await using (var setup = Context(tenantId))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Tenants.Add(new Tenant { Id = tenantId, Name = $"Acme {tenantId:N}", CreatedAt = at });
            setup.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "owner@acme.test", DisplayName = "Owner", CreatedAt = at });
            setup.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Locked", CreatedAt = at, CreatedByUserId = userId });
            await setup.SaveChangesAsync();
        }

        // The first finalize holds the document's lock inside its transaction and files version 1.
        await using var first = Context(tenantId);
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        Assert.Equal(1, await DocumentVersionNumbers.NextAsync(first, documentId, CancellationToken.None));
        first.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = new string('a', 64),
            ObjectKey = $"tenants/{tenantId:N}/2026/{Guid.NewGuid():N}/content.txt",
            CreatedByUserId = userId,
            CreatedAt = at,
            SizeBytes = 1,
        });
        await first.SaveChangesAsync();

        // A second allocation must WAIT: while the first transaction is open it cannot even read the highest number.
        await using var second = Context(tenantId);
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var waiting = DocumentVersionNumbers.NextAsync(second, documentId, CancellationToken.None);
        var finishedEarly = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500))) == waiting;
        Assert.False(finishedEarly, "the second allocation completed while the first still held the document's lock");

        // Commit the first; the waiting allocation then proceeds and sees the filed version.
        await firstTransaction.CommitAsync();
        Assert.Equal(2, await waiting);
        await secondTransaction.CommitAsync();
    }
}
