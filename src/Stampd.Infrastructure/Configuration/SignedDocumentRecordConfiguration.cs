using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class SignedDocumentRecordConfiguration : IEntityTypeConfiguration<SignedDocumentRecord>
{
    public void Configure(EntityTypeBuilder<SignedDocumentRecord> builder)
    {
        builder.ToTable("SignedDocumentRecords");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.StorageKey).IsRequired().HasMaxLength(512);
        builder.Property(d => d.ContentSha256).IsRequired().HasMaxLength(64);
        builder.Property(d => d.SignedAtUtc).IsRequired();
        builder.Property(d => d.SealingProviderName).IsRequired().HasMaxLength(64);
        builder.Property(d => d.SealingKeyIdentifier).IsRequired().HasMaxLength(512);
        builder.Property(d => d.TimestampAuthorityUrl).HasMaxLength(512);
        builder.Property(d => d.PAdESLevel).IsRequired().HasConversion<int>();

        builder.HasIndex(d => new { d.TenantId, d.SignedAtUtc });
        builder.HasIndex(d => d.SigningRequestId).IsUnique();
        builder.HasIndex(d => d.ContentSha256);
    }
}
