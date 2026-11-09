using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Alcifra.Infrastructure.Search;

namespace Alcifra.Infrastructure.Persistence.Configurations;

public class SearchReindexRunConfiguration : IEntityTypeConfiguration<SearchReindexRun>
{
    public void Configure(EntityTypeBuilder<SearchReindexRun> builder)
    {
        builder.HasKey(r => r.Id);

        // "The latest run" and "the oldest queued one" both read by request time.
        builder.HasIndex(r => r.RequestedAt);

        builder.Property(r => r.Failure).HasMaxLength(2000);
        builder.Property(r => r.Instance).HasMaxLength(255);

        // No tenant FK and no ITenantScoped filter: the index spans every tenant — see SearchReindexRun.
    }
}
