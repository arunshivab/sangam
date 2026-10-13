using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="ClientSecret"/> (rc.11).</summary>
internal sealed class ClientSecretConfiguration : IEntityTypeConfiguration<ClientSecret>
{
    public void Configure(EntityTypeBuilder<ClientSecret> b)
    {
        b.ToTable("client_secrets");
        b.HasKey(s => s.Id);
        b.Property(s => s.Hash).HasMaxLength(500).IsRequired();
        b.Property(s => s.Hint).HasMaxLength(40).IsRequired();
        b.HasOne<App>().WithMany().HasForeignKey(s => s.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(s => new { s.AppId, s.RevokedAt, s.ExpiresAt }).HasDatabaseName("idx_client_secrets_app_live");
    }
}
