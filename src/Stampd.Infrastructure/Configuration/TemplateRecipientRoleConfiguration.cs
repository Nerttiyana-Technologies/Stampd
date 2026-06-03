using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class TemplateRecipientRoleConfiguration : IEntityTypeConfiguration<TemplateRecipientRole>
{
    public void Configure(EntityTypeBuilder<TemplateRecipientRole> builder)
    {
        builder.ToTable("TemplateRecipientRoles");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).IsRequired().HasMaxLength(128);
        builder.Property(r => r.RoutingOrder).IsRequired();
        builder.Property(r => r.RequiresIdentityVerification).IsRequired();

        builder.HasIndex(r => new { r.DocumentTemplateId, r.RoutingOrder });

        builder.HasMany(r => r.Fields)
            .WithOne(f => f.AssignedRole)
            .HasForeignKey(f => f.AssignedRoleId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
