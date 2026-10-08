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
        b.Property(a => a.BrandColour).HasMaxLength(9).IsRequired();
        b.Property(a => a.Glyph).HasMaxLength(4).IsRequired();
        b.Property(a => a.IsPlatform).HasDefaultValue(false).IsRequired();
        b.Property(a => a.ConsentVersion).HasMaxLength(20).IsRequired();
        b.Property(a => a.SignInPolicy).HasConversion(new SnakeCaseEnumConverter<SignInPolicy>()).HasMaxLength(20).IsRequired();
        b.Property(a => a.MfaRequirement).HasConversion(new SnakeCaseEnumConverter<MfaRequirement>()).HasMaxLength(40).HasDefaultValue(MfaRequirement.Optional).IsRequired();
        b.Property(a => a.BreachedPasswordCheck).HasDefaultValue(false).IsRequired();

        b.HasIndex(a => a.ClientId).IsUnique().HasDatabaseName("ux_apps_client_id");
        b.HasIndex(a => a.Slug).IsUnique().HasDatabaseName("ux_apps_slug");

        b.ToTable(t =>
        {
            t.HasCheckConstraint("chk_apps_status", $"status IN ({SnakeCaseEnumConverter<AppStatus>.SqlList()})");
            t.HasCheckConstraint("chk_apps_sign_in_policy", $"sign_in_policy IN ({SnakeCaseEnumConverter<SignInPolicy>.SqlList()})");
            t.HasCheckConstraint("chk_apps_mfa_requirement", $"mfa_requirement IN ({SnakeCaseEnumConverter<MfaRequirement>.SqlList()})");
            t.HasCheckConstraint("chk_apps_min_password_length", $"min_password_length IS NULL OR min_password_length BETWEEN 8 AND {Domain.SecurityPolicy.MaxMinPasswordLength}");
        });
    }
}
