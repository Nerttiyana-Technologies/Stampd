using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class AdminScopeConfiguration : IEntityTypeConfiguration<AdminScope>
{
    public void Configure(EntityTypeBuilder<AdminScope> builder)
    {
        builder.ToTable("AdminScopes");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.UserId).IsRequired().HasMaxLength(256);
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.GrantedAtUtc).IsRequired();
        builder.Property(s => s.GrantedByUserId).IsRequired().HasMaxLength(256);
        builder.Property(s => s.RevokedByUserId).HasMaxLength(256);
        builder.Property(s => s.RevocationReason).HasMaxLength(1024);
        builder.Property(s => s.ConcurrencyToken).IsConcurrencyToken().IsRequired();

        // Hot path: "give me all active scopes for the current request's caller."
        // Composite on (UserId, TenantId) covers it; RevokedAtUtc is filtered in the
        // WHERE clause but isn't worth its own column index given the cardinality.
        builder.HasIndex(s => new { s.UserId, s.TenantId });

        // Secondary path: "give me everyone who has admin on tenant X." Used by the
        // GET /api/admin/scopes endpoint and the audit UI.
        builder.HasIndex(s => s.TenantId);
    }
}
