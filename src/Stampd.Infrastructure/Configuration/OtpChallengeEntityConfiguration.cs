using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Infrastructure.Identity;

namespace Stampd.Infrastructure.Configuration;

internal sealed class OtpChallengeEntityConfiguration : IEntityTypeConfiguration<OtpChallengeEntity>
{
    public void Configure(EntityTypeBuilder<OtpChallengeEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OtpChallenges");
        builder.HasKey(c => c.VerificationId);
        builder.Property(c => c.VerificationId).HasMaxLength(64);
        builder.Property(c => c.Identifier).HasMaxLength(320).IsRequired();
        builder.Property(c => c.CodeHash).HasMaxLength(128).IsRequired();
        builder.Property(c => c.Salt).HasMaxLength(64).IsRequired();
        builder.Property(c => c.ExpiresAtUtc).IsRequired();
        builder.Property(c => c.CreatedAtUtc).IsRequired();

        // Lookup by expiry for the cleanup background sweep.
        builder.HasIndex(c => c.ExpiresAtUtc);
    }
}
