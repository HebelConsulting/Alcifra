using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Imap;
using Alcifra.Domain.Tenants;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// See ADR "IMAP endpoint (read-only, first slice)". Composite (FolderId, DocumentId) key — a document holds
// one stable UID per mailbox it appears in; unique (FolderId, Uid) is the RFC 3501 guarantee that a UID
// names at most one message. Both FKs cascade: a purged folder or document takes its UID rows along (the
// UIDs are never reused because ImapMailbox.NextUid only ever grows).
public class ImapMessageUidConfiguration : IEntityTypeConfiguration<ImapMessageUid>
{
    public void Configure(EntityTypeBuilder<ImapMessageUid> builder)
    {
        builder.HasKey(u => new { u.FolderId, u.DocumentId });

        builder.HasIndex(u => new { u.FolderId, u.Uid }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ImapMailbox>()
            .WithMany()
            .HasForeignKey(u => u.FolderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(u => u.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
