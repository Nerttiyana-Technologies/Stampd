using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class BulkSendJobConfiguration : IEntityTypeConfiguration<BulkSendJob>
{
    public void Configure(EntityTypeBuilder<BulkSendJob> builder)
    {
        builder.ToTable("BulkSendJobs");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Status).IsRequired().HasConversion<int>();
        builder.Property(j => j.Subject).IsRequired().HasMaxLength(512);
        builder.Property(j => j.Message).HasMaxLength(4096);
        builder.Property(j => j.PendingRowsJson).IsRequired();
        builder.Property(j => j.FailedRowsJson).IsRequired();
        builder.Property(j => j.CreatedAtUtcEpochMs).IsRequired();

        builder.HasIndex(j => new { j.TenantId, j.Status });
        // BulkSendWorker.FindNextJobAsync uses (Status, CreatedAtUtcEpochMs) to pick the
        // oldest pending job in O(log n) via this composite index.
        builder.HasIndex(j => new { j.Status, j.CreatedAtUtcEpochMs });
    }
}
