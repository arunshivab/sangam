using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="SangamUser"/> and the three Identity satellite tables to snake_case names.</summary>
internal sealed class SangamUserConfiguration :
    IEntityTypeConfiguration<SangamUser>,
    IEntityTypeConfiguration<IdentityUserClaim<Guid>>,
    IEntityTypeConfiguration<IdentityUserLogin<Guid>>,
    IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<SangamUser> b)
    {
        b.ToTable("users");
        b.Property(u => u.FirstName).HasMaxLength(100).IsRequired();
        b.Property(u => u.LastName).HasMaxLength(100).IsRequired();
        b.Property(u => u.DateOfBirth).HasColumnType("date").IsRequired();
        b.Property(u => u.Gender).HasConversion(new SnakeCaseEnumConverter<Gender>()).HasMaxLength(20).IsRequired();
        b.Property(u => u.SignInPreference).HasConversion(new SnakeCaseEnumConverter<SignInMode>()).HasMaxLength(20).IsRequired();
        b.Ignore(u => u.DisplayName);
        b.Property(u => u.Locale).HasMaxLength(10).IsRequired();
        b.Property(u => u.TimeZone).HasMaxLength(50).IsRequired();
        b.Property(u => u.Status).HasConversion(new SnakeCaseEnumConverter<UserStatus>()).HasMaxLength(20).IsRequired();
        b.Property(u => u.Email).HasMaxLength(256).IsRequired();
        b.Property(u => u.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(u => u.PhoneNumber).HasColumnName("mobile").HasMaxLength(20);
        b.Property(u => u.PhoneNumberConfirmed).HasColumnName("mobile_verified");
        b.Property(u => u.PasswordHash).HasMaxLength(500);

        // Email is the identity: unique, case-insensitive through NormalizedEmail.
        b.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
        b.HasIndex(u => u.NormalizedUserName).IsUnique().HasDatabaseName("ux_users_normalized_user_name");
        // Personal mobile is unique among users that still exist.
        b.HasIndex(u => u.PhoneNumber)
            .IsUnique()
            .HasFilter("mobile IS NOT NULL AND status <> 'deleted_hard'")
            .HasDatabaseName("ux_users_mobile");
        b.Property(u => u.HoldReason).HasMaxLength(500);
        b.HasIndex(u => u.Status).HasDatabaseName("idx_users_status");
        b.HasIndex(u => u.PurgeAfter).HasFilter("purge_after IS NOT NULL").HasDatabaseName("idx_users_purge_after");

        b.ToTable(t =>
        {
            t.HasCheckConstraint("chk_users_status", $"status IN ({SnakeCaseEnumConverter<UserStatus>.SqlList()})");
            t.HasCheckConstraint("chk_users_gender", $"gender IN ({SnakeCaseEnumConverter<Gender>.SqlList()})");
            t.HasCheckConstraint("chk_users_sign_in_preference", $"sign_in_preference IN ({SnakeCaseEnumConverter<SignInMode>.SqlList()})");
        });
    }

    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> b) => b.ToTable("user_claims");

    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> b) => b.ToTable("user_logins");

    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> b) => b.ToTable("user_tokens");
}
