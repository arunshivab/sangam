using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class SamlServiceProviderConfiguration : IEntityTypeConfiguration<SamlServiceProvider>
{
    public void Configure(EntityTypeBuilder<SamlServiceProvider> b)
    {
        b.ToTable("saml_service_providers");
        b.HasKey(p => p.Id);
        b.Property(p => p.EntityId).HasMaxLength(500).IsRequired();
        b.HasIndex(p => p.EntityId).IsUnique().HasDatabaseName("ux_saml_service_providers_entity_id");
        b.HasIndex(p => p.AppId).IsUnique().HasDatabaseName("ux_saml_service_providers_app_id");
        b.Property(p => p.AcsUrls).HasMaxLength(4000).IsRequired();
        b.Property(p => p.SloUrl).HasMaxLength(1000);
        b.Property(p => p.SigningCertificate).HasMaxLength(8000);
        b.Property(p => p.EncryptionCertificate).HasMaxLength(8000);
        b.Property(p => p.NameIdFormat).HasMaxLength(20).IsRequired();
        b.Property(p => p.Attributes).HasMaxLength(200).IsRequired();
        b.Property(p => p.DefaultRelayState).HasMaxLength(1000);
        b.HasOne<App>().WithMany().HasForeignKey(p => p.AppId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SamlRequestConfiguration : IEntityTypeConfiguration<SamlRequest>
{
    public void Configure(EntityTypeBuilder<SamlRequest> b)
    {
        b.ToTable("saml_requests");
        b.HasKey(r => r.Handle);
        b.Property(r => r.Handle).HasMaxLength(64);
        b.Property(r => r.RequestId).HasMaxLength(256).IsRequired();
        b.Property(r => r.AcsUrl).HasMaxLength(1000).IsRequired();
        b.Property(r => r.RelayState).HasMaxLength(1000);
        // Replay: one answer per request id per SP (IdP-initiated requests have no id and are exempt).
        b.HasIndex(r => new { r.ServiceProviderId, r.RequestId }).IsUnique().HasFilter("request_id <> ''").HasDatabaseName("ux_saml_requests_sp_request");
        b.HasIndex(r => r.ExpiresAt).HasDatabaseName("idx_saml_requests_expires");
    }
}
