using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="AppAdmin"/>.</summary>
internal sealed class AppAdminConfiguration : IEntityTypeConfiguration<AppAdmin>
{
    public void Configure(EntityTypeBuilder<AppAdmin> b)
    {
        b.ToTable("app_admins");
        b.HasKey(a => a.Id);
        b.HasOne(a => a.App).WithMany().HasForeignKey(a => a.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(a => new { a.AppId, a.UserId }).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("ux_app_admins_active");
    }
}
