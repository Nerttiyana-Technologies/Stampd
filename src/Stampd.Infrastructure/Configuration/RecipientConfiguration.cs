using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class RecipientConfiguration : IEntityTypeConfiguration<Recipient>
{
    public void Configure(EntityTypeBuilder<Recipient> builder)
    {
        builder.ToTable("Recipients");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Email).IsRequired().HasMaxLength(320); // RFC 5321 max
        builder.Property(r => r.Name).IsRequired().HasMaxLength(256);
        builder.Property(r => r.RoutingOrder).IsRequired();
        builder.Property(r => r.Status).IsRequired().HasConversion<int>();
        builder.Property(r => r.IdentityVerificationMethod).HasMaxLength(64);
        builder.Property(r => r.DeclineReason).HasMaxLength(2048);
        builder.Property(r => r.AccessToken).IsRequired().HasMaxLength(128);

        builder.HasIndex(r => new { r.SigningRequestId, r.RoutingOrder });
        builder.HasIndex(r => r.AccessToken).IsUnique();

        builder.HasOne(r => r.Role)
            .WithMany()
            .HasForeignKey(r => r.RoleId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
