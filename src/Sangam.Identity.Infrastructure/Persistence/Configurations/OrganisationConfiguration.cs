using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Organisation"/>: the tree, the materialised path and the type FK.</summary>
internal sealed class OrganisationConfiguration : IEntityTypeConfiguration<Organisation>
{
    public void Configure(EntityTypeBuilder<Organisation> b)
    {
        b.ToTable("organisations");
        b.HasKey(o => o.Id);
        b.Property(o => o.Name).HasMaxLength(200).IsRequired();
        b.Property(o => o.OrgTypeCode).HasMaxLength(50).IsRequired();
        b.Property(o => o.Path).HasMaxLength(2000).IsRequired();
        b.Property(o => o.Metadata).HasColumnType("jsonb").IsRequired();
        b.Property(o => o.Status).HasConversion(new SnakeCaseEnumConverter<OrganisationStatus>()).HasMaxLength(20).IsRequired();

        b.HasOne(o => o.OrgType).WithMany().HasForeignKey(o => o.OrgTypeCode).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(o => o.Parent).WithMany().HasForeignKey(o => o.ParentOrgId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(o => o.RegisteredViaApp).WithMany().HasForeignKey(o => o.RegisteredViaAppId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(o => o.Path).HasDatabaseName("idx_organisations_path");
        b.HasIndex(o => o.ParentOrgId).HasDatabaseName("idx_organisations_parent");
        b.HasIndex(o => o.OrgTypeCode).HasDatabaseName("idx_organisations_type");
        b.HasIndex(o => o.RegisteredViaAppId).HasDatabaseName("idx_organisations_registered_via_app");

        b.ToTable(t =>
        {
            t.HasCheckConstraint(
                "chk_organisations_status",
                $"status IN ({SnakeCaseEnumConverter<OrganisationStatus>.SqlList()})");
            t.HasCheckConstraint("chk_organisations_depth", "(parent_org_id IS NULL AND depth = 0) OR (parent_org_id IS NOT NULL AND depth > 0)");
        });
    }
}
