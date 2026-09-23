using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="AppGrant"/>.</summary>
internal sealed class AppGrantConfiguration : IEntityTypeConfiguration<AppGrant>
{
    public void Configure(EntityTypeBuilder<AppGrant> b)
    {
        b.ToTable("app_grants");
        b.HasKey(g => g.Id);
        b.HasOne(g => g.User).WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(g => g.App).WithMany().HasForeignKey(g => g.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(g => new { g.UserId, g.AppId })
            .IsUnique()
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ux_app_grants_active");
        b.HasIndex(g => g.UserId).HasFilter("revoked_at IS NULL").HasDatabaseName("idx_app_grants_user");
    }
}
