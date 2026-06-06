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

        // v2.0 Slice D — actor attribution. Nullable strings; capped to reasonable
        // lengths so the column doesn't bloat the table. ActorUserId at 256 holds any
        // reasonable JWT sub claim (email, UUID, opaque identifier); ActorRole at 64
        // covers Admin/Sender/ReadOnly with headroom for future role names.
        builder.Property(e => e.ActorUserId).HasMaxLength(256);
        builder.Property(e => e.ActorRole).HasMaxLength(64);

        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.SigningRequestId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.EventType });
        // Filtered index for "all admin actions in this tenant" — the most common
        // audit query a tenant admin will run. NULL entries (recipient-flow events,
        // pre-v2 rows) are excluded.
        builder.HasIndex(e => new { e.TenantId, e.ActorUserId, e.OccurredAtUtc });

        builder.HasOne(e => e.Recipient)
            .WithMany()
            .HasForeignKey(e => e.RecipientId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
