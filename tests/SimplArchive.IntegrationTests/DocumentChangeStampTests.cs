using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Document.ChangedAt (ADR 0925): stamped centrally by SaveChanges whenever what a reader sees of a document changes,
// so "changes since" can page by it. A stamp reached only through a related row (a chat message here) must NOT move
// the document's ETag: somebody editing the details would otherwise get 412 because somebody else chatted.
public sealed class DocumentChangeStampTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly Guid _tenantId = Guid.NewGuid(), _userId = Guid.NewGuid(), _documentId = Guid.NewGuid();
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private SimplArchiveDbContext Context(Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(_connection).Options, new CurrentTenantAccessor { TenantId = tenantId });

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var setup = Context();
        await setup.Database.EnsureCreatedAsync();
        setup.Tenants.Add(new Tenant { Id = _tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        setup.Users.Add(new User { Id = _userId, TenantId = _tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        setup.Documents.Add(new Document { Id = _documentId, TenantId = _tenantId, Name = "Invoice", CreatedByUserId = _userId, CreatedAt = DateTimeOffset.UtcNow });
        await setup.SaveChangesAsync();

        // Back-date the stamp outside SaveChanges, so every test sees whether ITS save moved it.
        await setup.Documents.IgnoreQueryFilters().Where(d => d.Id == _documentId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.ChangedAt, LongAgo));
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task<(DateTimeOffset ChangedAt, Guid Token)> ReadAsync()
    {
        using var read = Context(_tenantId);
        var document = await read.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).SingleAsync(d => d.Id == _documentId);
        return (document.ChangedAt, document.ConcurrencyToken);
    }

    [Fact]
    public async Task A_new_document_is_stamped()
    {
        using var context = Context(_tenantId);
        var id = Guid.NewGuid();
        context.Documents.Add(new Document { Id = id, TenantId = _tenantId, Name = "New", CreatedByUserId = _userId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        Assert.True((await context.Documents.SingleAsync(d => d.Id == id)).ChangedAt > LongAgo);
    }

    [Fact]
    public async Task A_rename_stamps_and_moves_the_etag()
    {
        var before = await ReadAsync();
        using (var context = Context(_tenantId))
        {
            (await context.Documents.SingleAsync(d => d.Id == _documentId)).Name = "Renamed";
            await context.SaveChangesAsync();
        }

        var after = await ReadAsync();
        Assert.True(after.ChangedAt > LongAgo);
        Assert.NotEqual(before.Token, after.Token);
    }

    [Fact]
    public async Task A_chat_message_stamps_its_document_and_keeps_the_etag()
    {
        var before = await ReadAsync();
        using (var context = Context(_tenantId))
        {
            context.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                DocumentId = _documentId,
                Body = "Looks fine",
                CreatedByUserId = _userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var after = await ReadAsync();
        Assert.True(after.ChangedAt > LongAgo);
        Assert.Equal(before.Token, after.Token);
    }

    [Fact]
    public async Task A_soft_deletion_stamps()
    {
        using (var context = Context(_tenantId))
        {
            (await context.Documents.SingleAsync(d => d.Id == _documentId)).DeletedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        Assert.True((await ReadAsync()).ChangedAt > LongAgo);
    }

    [Fact]
    public async Task A_save_that_touches_nothing_of_the_document_does_not_stamp_it()
    {
        using (var context = Context(_tenantId))
        {
            context.Users.Single(u => u.Id == _userId).DisplayName = "B";
            await context.SaveChangesAsync();
        }

        Assert.Equal(LongAgo, (await ReadAsync()).ChangedAt);
    }
}
