using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OrgMembership"/> with the partial unique index that allows re-grants after revocation.</summary>
internal sealed class OrgMembershipConfiguration : IEntityTypeConfiguration<OrgMembership>
{
    public void Configure(EntityTypeBuilder<OrgMembership> b)
    {
        b.ToTable("org_memberships");
        b.HasKey(m => m.Id);

        b.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.Org).WithMany().HasForeignKey(m => m.OrgId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.App).WithMany().HasForeignKey(m => m.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.Role).WithMany().HasForeignKey(m => m.RoleId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(m => new { m.UserId, m.OrgId, m.AppId })
            .IsUnique()
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ux_org_memberships_active");
        b.HasIndex(m => m.UserId).HasFilter("revoked_at IS NULL").HasDatabaseName("idx_org_memberships_user");
        b.HasIndex(m => m.OrgId).HasFilter("revoked_at IS NULL").HasDatabaseName("idx_org_memberships_org");
        b.HasIndex(m => new { m.UserId, m.AppId }).HasFilter("revoked_at IS NULL").HasDatabaseName("idx_org_memberships_user_app");
    }
}
