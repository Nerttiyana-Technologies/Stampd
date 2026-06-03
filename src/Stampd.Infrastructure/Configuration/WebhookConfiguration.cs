using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Stampd.Core.Entities;

namespace Stampd.Infrastructure.Configuration;

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("WebhookEndpoints");
        builder.HasKey(w => w.Id);

        builder.Property(w => w.Url).IsRequired().HasMaxLength(2048);
        builder.Property(w => w.Secret).IsRequired().HasMaxLength(128);
        builder.Property(w => w.SubscribedEvents).IsRequired().HasMaxLength(512);
        builder.Property(w => w.IsActive).IsRequired();

        builder.HasIndex(w => new { w.TenantId, w.IsActive });
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("WebhookDeliveries");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.EventType).IsRequired().HasConversion<int>();
        builder.Property(d => d.PayloadJson).IsRequired();
        builder.Property(d => d.LastErrorMessage).HasMaxLength(2048);

        builder.HasIndex(d => d.NextAttemptAtUtc);
        builder.HasIndex(d => d.WebhookEndpointId);

        builder.HasOne(d => d.WebhookEndpoint)
            .WithMany()
            .HasForeignKey(d => d.WebhookEndpointId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
