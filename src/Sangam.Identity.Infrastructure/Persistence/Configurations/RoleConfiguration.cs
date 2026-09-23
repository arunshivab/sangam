using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Role"/>. Code is unique per (app, org scope); NULL org counts as one scope.</summary>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.HasKey(r => r.Id);
        b.Property(r => r.Code).HasMaxLength(50).IsRequired();
        b.Property(r => r.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(r => r.Description).HasMaxLength(500);
        b.Property(r => r.Permissions).HasColumnType("jsonb").IsRequired();

        b.HasOne(r => r.App).WithMany().HasForeignKey(r => r.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(r => r.Org).WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(r => new { r.AppId, r.OrgId, r.Code })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_roles_app_org_code");
        b.HasIndex(r => r.AppId).HasDatabaseName("idx_roles_app");
    }
}
