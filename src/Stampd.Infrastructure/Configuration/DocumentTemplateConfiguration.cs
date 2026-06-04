using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class DocumentTemplateConfiguration : IEntityTypeConfiguration<DocumentTemplate>
{
    public void Configure(EntityTypeBuilder<DocumentTemplate> builder)
    {
        builder.ToTable("DocumentTemplates");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.Name).IsRequired().HasMaxLength(256);
        builder.Property(t => t.Description).HasMaxLength(2048);
        builder.Property(t => t.SourcePdfStorageKey).IsRequired().HasMaxLength(512);
        builder.Property(t => t.SourcePdfSha256).IsRequired().HasMaxLength(64);
        builder.Property(t => t.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc).IsRequired();
        builder.Property(t => t.IsArchived).IsRequired();
        builder.Property(t => t.ConcurrencyToken).IsConcurrencyToken().IsRequired();
        builder.Property(t => t.CreatedAtUtcEpochMs).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.Name });
        builder.HasIndex(t => new { t.TenantId, t.IsArchived });
        // Tenant-scoped newest-first listing in GET /api/templates uses this index to
        // ORDER BY CreatedAtUtcEpochMs DESC server-side.
        builder.HasIndex(t => new { t.TenantId, t.CreatedAtUtcEpochMs });

        builder.HasMany(t => t.Roles)
            .WithOne(r => r.DocumentTemplate)
            .HasForeignKey(r => r.DocumentTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Fields)
            .WithOne(f => f.DocumentTemplate)
            .HasForeignKey(f => f.DocumentTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tenant query filter is declared centrally in StampdDbContext.OnModelCreating so
        // the lambda can close over the live DbContext-bound tenant accessor.
    }
}
