using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Groups;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;

namespace Alcifra.Infrastructure.Persistence.Configurations;

// ADR 0983. One member of a sealed item's declared audience: exactly one principal (user or group), a manager only ever
// a user. Cascade from the declaring document and from the principal, the SavedSearchShare shape.
public class ContentAudienceMemberConfiguration : IEntityTypeConfiguration<ContentAudienceMember>
{
    public void Configure(EntityTypeBuilder<ContentAudienceMember> builder)
    {
        builder.HasKey(m => m.Id);

        // No principal twice in one audience — one partial unique index per principal type (AclEntry's NULL != NULL
        // reasoning). The one-manager-only index of the first schema draft is deliberately absent: there may be many.
        builder.HasIndex(m => new { m.TenantId, m.DocumentId, m.UserId })
            .IsUnique()
            .HasFilter("\"UserId\" IS NOT NULL");
        builder.HasIndex(m => new { m.TenantId, m.DocumentId, m.GroupId })
            .IsUnique()
            .HasFilter("\"GroupId\" IS NOT NULL");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ContentAudienceMembers_ExactlyOnePrincipal",
                "(CASE WHEN \"UserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
                "CASE WHEN \"GroupId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
            // Who may hand out decryption is always a named person, never "whoever is in that group".
            t.HasCheckConstraint(
                "CK_ContentAudienceMembers_ManagerIsAUser",
                "NOT \"IsManager\" OR \"UserId\" IS NOT NULL");
        });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(m => m.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Group>()
            .WithMany()
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
