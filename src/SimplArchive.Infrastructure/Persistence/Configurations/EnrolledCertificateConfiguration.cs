using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Certificates;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// The core's certificate registry (#1790, ADR 0935): one row per Certificate document, derived from it. Keyed by
// the document, cascading with it (a purged certificate document takes its row) and with its holder. The
// (TenantId, UserId) index answers "this reader's certificates"; (TenantId, Thumbprint) is unique, so a key is
// enrolled once per tenant (the Encryption Module's own rule, carried over).
public class EnrolledCertificateConfiguration : IEntityTypeConfiguration<EnrolledCertificate>
{
    public void Configure(EntityTypeBuilder<EnrolledCertificate> builder)
    {
        builder.HasKey(c => c.DocumentId);
        builder.Property(c => c.DocumentId).ValueGeneratedNever();
        builder.Property(c => c.CertificatePem).IsRequired();
        builder.Property(c => c.Label).IsRequired().HasMaxLength(500);
        builder.Property(c => c.Thumbprint).IsRequired().HasMaxLength(64);
        builder.Property(c => c.Serial).IsRequired().HasMaxLength(200);

        builder.HasIndex(c => new { c.TenantId, c.UserId });
        builder.HasIndex(c => new { c.TenantId, c.Thumbprint }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
