using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.ToTable("webhook_endpoints", t => t.HasCheckConstraint("chk_webhook_endpoints_status", "status IN ('ok','failing')"));
        b.HasKey(e => e.Id);
        b.Property(e => e.Url).HasMaxLength(500).IsRequired();
        b.Property(e => e.Description).HasMaxLength(200);
        b.Property(e => e.Events).HasMaxLength(500).IsRequired();
        b.Property(e => e.ProtectedSecret).HasMaxLength(2000).IsRequired();
        b.Property(e => e.ProtectedPreviousSecret).HasMaxLength(2000);
        b.Property(e => e.Status).HasMaxLength(10);
        b.HasIndex(e => e.AppId).HasDatabaseName("idx_webhook_endpoints_app");
        b.HasOne<App>().WithMany().HasForeignKey(e => e.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("webhook_deliveries", t => t.HasCheckConstraint("chk_webhook_deliveries_status", "status IN ('pending','done','dead')"));
        b.HasKey(d => d.Id);
        b.Property(d => d.Id).UseIdentityAlwaysColumn();
        b.Property(d => d.EventType).HasMaxLength(40);
        b.Property(d => d.Payload).HasMaxLength(8000).IsRequired();
        b.Property(d => d.Status).HasMaxLength(10);
        b.Property(d => d.ResponseSnippet).HasMaxLength(500);
        b.Property(d => d.LastError).HasMaxLength(1000);
        b.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'pending'").HasDatabaseName("idx_webhook_deliveries_due");
        b.HasIndex(d => new { d.EndpointId, d.CreatedAt }).HasDatabaseName("idx_webhook_deliveries_endpoint_created");
        b.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(d => d.EndpointId).OnDelete(DeleteBehavior.Cascade);
    }
}
