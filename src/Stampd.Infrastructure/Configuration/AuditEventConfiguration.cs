using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.EventType).IsRequired().HasConversion<int>();
        builder.Property(e => e.OccurredAtUtc).IsRequired();
        builder.Property(e => e.IpAddress).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(512);
        builder.Property(e => e.GeoCountry).HasMaxLength(64);
        builder.Property(e => e.GeoCity).HasMaxLength(128);
        builder.Property(e => e.DocumentHashAtEvent).HasMaxLength(64);

        // PayloadJson stored as a plain string on the agnostic configuration; the Postgres
        // provider remaps it to jsonb in its provider-specific configuration project.
        builder.Property(e => e.PayloadJson);

        builder.Property(e => e.IsRedacted).IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.SigningRequestId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.EventType });

        builder.HasOne(e => e.Recipient)
            .WithMany()
            .HasForeignKey(e => e.RecipientId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
