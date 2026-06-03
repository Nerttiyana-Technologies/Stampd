using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class TemplateFieldConfiguration : IEntityTypeConfiguration<TemplateField>
{
    public void Configure(EntityTypeBuilder<TemplateField> builder)
    {
        builder.ToTable("TemplateFields");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.PageNumber).IsRequired();
        builder.Property(f => f.BoundsX).IsRequired();
        builder.Property(f => f.BoundsY).IsRequired();
        builder.Property(f => f.BoundsWidth).IsRequired();
        builder.Property(f => f.BoundsHeight).IsRequired();
        builder.Property(f => f.Kind).IsRequired().HasConversion<int>();
        builder.Property(f => f.IsRequired).IsRequired();
        builder.Property(f => f.Label).HasMaxLength(256);
        builder.Property(f => f.DefaultValue).HasMaxLength(2048);

        builder.HasIndex(f => new { f.DocumentTemplateId, f.PageNumber });
    }
}
