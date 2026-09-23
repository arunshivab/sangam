using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="App"/>.</summary>
internal sealed class AppConfiguration : IEntityTypeConfiguration<App>
{
    public void Configure(EntityTypeBuilder<App> b)
    {
        b.ToTable("apps");
        b.HasKey(a => a.Id);
        b.Property(a => a.ClientId).HasMaxLength(100).IsRequired();
        b.Property(a => a.Slug).HasMaxLength(50).IsRequired();
        b.Property(a => a.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(a => a.OwnerCompanyName).HasMaxLength(200).IsRequired();
        b.Property(a => a.Description).HasMaxLength(500);
        b.Property(a => a.HomepageUrl).HasMaxLength(500);
        b.Property(a => a.PrivacyUrl).HasMaxLength(500);
        b.Property(a => a.TermsUrl).HasMaxLength(500);
        b.Property(a => a.Status).HasConversion(new SnakeCaseEnumConverter<AppStatus>()).HasMaxLength(20).IsRequired();

        b.HasIndex(a => a.ClientId).IsUnique().HasDatabaseName("ux_apps_client_id");
        b.HasIndex(a => a.Slug).IsUnique().HasDatabaseName("ux_apps_slug");

        b.ToTable(t => t.HasCheckConstraint(
            "chk_apps_status",
            $"status IN ({SnakeCaseEnumConverter<AppStatus>.SqlList()})"));
    }
}
