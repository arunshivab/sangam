using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class GrievanceConfiguration : IEntityTypeConfiguration<Grievance>
{
    public void Configure(EntityTypeBuilder<Grievance> b)
    {
        b.ToTable("grievances", t => t.HasCheckConstraint("chk_grievances_status", $"status IN ({SnakeCaseEnumConverter<GrievanceStatus>.SqlList()})"));
        b.HasKey(g => g.Id);
        b.Property(g => g.Reference).HasMaxLength(20).IsRequired();
        b.HasIndex(g => g.Reference).IsUnique().HasDatabaseName("ux_grievances_reference");
        b.Property(g => g.Channel).HasMaxLength(20).IsRequired();
        b.Property(g => g.Category).HasMaxLength(20).IsRequired();
        b.Property(g => g.ComplainantName).HasMaxLength(200).IsRequired();
        b.Property(g => g.ComplainantContact).HasMaxLength(320).IsRequired();
        b.Property(g => g.Summary).HasMaxLength(4000).IsRequired();
        b.Property(g => g.Status).HasConversion(new SnakeCaseEnumConverter<GrievanceStatus>()).HasMaxLength(20).IsRequired();
        b.Property(g => g.Resolution).HasMaxLength(4000);
        b.HasIndex(g => new { g.Status, g.ResolveBy }).HasDatabaseName("idx_grievances_status_due");
        b.HasMany(g => g.Entries).WithOne().HasForeignKey(e => e.GrievanceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GrievanceEntryConfiguration : IEntityTypeConfiguration<GrievanceEntry>
{
    public void Configure(EntityTypeBuilder<GrievanceEntry> b)
    {
        b.ToTable("grievance_entries");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).UseIdentityAlwaysColumn();
        b.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        b.Property(e => e.Text).HasMaxLength(4000).IsRequired();
        b.HasIndex(e => new { e.GrievanceId, e.At }).HasDatabaseName("idx_grievance_entries_grievance");
    }
}
