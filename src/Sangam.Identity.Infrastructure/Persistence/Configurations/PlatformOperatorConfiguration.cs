using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="PlatformOperator"/>.</summary>
internal sealed class PlatformOperatorConfiguration : IEntityTypeConfiguration<PlatformOperator>
{
    public void Configure(EntityTypeBuilder<PlatformOperator> b)
    {
        b.ToTable("platform_operators");
        b.HasKey(p => p.Id);
        b.Property(p => p.Role).HasConversion(new SnakeCaseEnumConverter<PlatformRole>()).HasMaxLength(20).IsRequired();
        b.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(p => p.UserId).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("ux_platform_operators_active");
    }
}
