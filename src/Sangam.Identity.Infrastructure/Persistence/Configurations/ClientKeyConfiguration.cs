using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="ClientKey"/> and <see cref="ClientAssertionUse"/> (rc.15).</summary>
internal sealed class ClientKeyConfiguration : IEntityTypeConfiguration<ClientKey>, IEntityTypeConfiguration<ClientAssertionUse>
{
    public void Configure(EntityTypeBuilder<ClientKey> b)
    {
        b.ToTable("client_keys");
        b.HasKey(k => k.Id);
        b.Property(k => k.KeyId).HasMaxLength(100).IsRequired();
        b.Property(k => k.Jwk).HasMaxLength(4000).IsRequired();
        b.Property(k => k.Kind).HasMaxLength(40).IsRequired();
        b.Property(k => k.Name).HasMaxLength(80).IsRequired();
        b.HasOne<App>().WithMany().HasForeignKey(k => k.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(k => new { k.AppId, k.RevokedAt }).HasDatabaseName("idx_client_keys_app_live");

        // The same key is live at most once for an application.
        b.HasIndex(k => new { k.AppId, k.KeyId }).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("ux_client_keys_live_key");
    }

    public void Configure(EntityTypeBuilder<ClientAssertionUse> b)
    {
        b.ToTable("client_assertion_uses");
        b.HasKey(u => new { u.ClientId, u.TokenId });
        b.Property(u => u.ClientId).HasMaxLength(100);
        b.Property(u => u.TokenId).HasMaxLength(200);
        b.HasIndex(u => u.ExpiresAt).HasDatabaseName("idx_client_assertion_uses_expires");
    }
}
