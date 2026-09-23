using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OrgType"/> and seeds the reference rows.</summary>
internal sealed class OrgTypeConfiguration : IEntityTypeConfiguration<OrgType>
{
    public void Configure(EntityTypeBuilder<OrgType> b)
    {
        b.ToTable("org_types");
        b.HasKey(t => t.Code);
        b.Property(t => t.Code).HasMaxLength(50);
        b.Property(t => t.DisplayName).HasMaxLength(200).IsRequired();

        b.HasData(
            new OrgType { Code = "corporate", DisplayName = "Corporate group", Description = "A holding entity that owns hospitals, clinics or labs.", CanBeRoot = true, CanHaveChildren = true, SortOrder = 10 },
            new OrgType { Code = "hospital", DisplayName = "Hospital", CanBeRoot = true, CanHaveChildren = true, SortOrder = 20 },
            new OrgType { Code = "clinic", DisplayName = "Clinic / Practice", CanBeRoot = true, CanHaveChildren = true, SortOrder = 30 },
            new OrgType { Code = "lab", DisplayName = "Diagnostic laboratory", CanBeRoot = true, CanHaveChildren = true, SortOrder = 40 },
            new OrgType { Code = "department", DisplayName = "Department", Description = "A unit inside a hospital, clinic or lab. Cannot be a root.", CanBeRoot = false, CanHaveChildren = false, SortOrder = 50 },
            new OrgType { Code = "construction_firm", DisplayName = "Construction firm", CanBeRoot = true, CanHaveChildren = true, SortOrder = 60 },
            new OrgType { Code = "education", DisplayName = "Educational institution", CanBeRoot = true, CanHaveChildren = true, SortOrder = 70 },
            new OrgType { Code = "other", DisplayName = "Other", CanBeRoot = true, CanHaveChildren = true, SortOrder = 99 });
    }
}
