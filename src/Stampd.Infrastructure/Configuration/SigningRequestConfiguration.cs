using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class SigningRequestConfiguration : IEntityTypeConfiguration<SigningRequest>
{
    public void Configure(EntityTypeBuilder<SigningRequest> builder)
    {
        builder.ToTable("SigningRequests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Subject).IsRequired().HasMaxLength(256);
        builder.Property(r => r.Message).HasMaxLength(4096);
        builder.Property(r => r.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(r => r.Status).IsRequired().HasConversion<int>();
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.TerminationReason).HasMaxLength(2048);
        builder.Property(r => r.ConcurrencyToken).IsConcurrencyToken().IsRequired();
        builder.Property(r => r.CreatedAtUtcEpochMs).IsRequired();
        // CompletedAtUtcEpochMs mirrors the nullability of CompletedAtUtc — null while
        // the workflow is in flight, populated by the SaveChanges interceptor once the
        // request transitions to Completed.
        builder.Property(r => r.CompletedAtUtcEpochMs);

        builder.HasIndex(r => new { r.TenantId, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.CreatedAtUtc });
        builder.HasIndex(r => r.DocumentTemplateId);
        // GET /api/signing-requests orders newest-first by this epoch column. The
        // (TenantId, CreatedAtUtcEpochMs) composite serves the optional ?templateId=
        // filter as well — DocumentTemplateId is correlated with TenantId.
        builder.HasIndex(r => new { r.TenantId, r.CreatedAtUtcEpochMs });
        // v2.1 #212 — covering index for the "completed" sort and any future
        // ?completedFrom=/?completedTo= range filter. Nulls (in-flight workflows)
        // are included; sort callers should treat them as the trailing bucket.
        builder.HasIndex(r => new { r.TenantId, r.CompletedAtUtcEpochMs });

        builder.HasOne(r => r.DocumentTemplate)
            .WithMany()
            .HasForeignKey(r => r.DocumentTemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Recipients)
            .WithOne(p => p.SigningRequest)
            .HasForeignKey(p => p.SigningRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.AuditEvents)
            .WithOne(e => e.SigningRequest)
            .HasForeignKey(e => e.SigningRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.SignedDocument)
            .WithOne(d => d.SigningRequest)
            .HasForeignKey<SignedDocumentRecord>(d => d.SigningRequestId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
