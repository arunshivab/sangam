using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OrgAdmin"/> (rc.12).</summary>
internal sealed class OrgAdminConfiguration : IEntityTypeConfiguration<OrgAdmin>
{
    public void Configure(EntityTypeBuilder<OrgAdmin> b)
    {
        b.ToTable("org_admins");
        b.HasKey(a => a.Id);
        b.Property(a => a.Right).HasConversion(new SnakeCaseEnumConverter<OrgAdminRight>()).HasMaxLength(30).IsRequired();
        b.HasOne(a => a.Org).WithMany().HasForeignKey(a => a.OrgId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<App>().WithMany().HasForeignKey(a => a.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(a => new { a.AppId, a.OrgId, a.UserId, a.Right }).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("ux_org_admins_active");
        b.HasIndex(a => a.UserId).HasFilter("revoked_at IS NULL").HasDatabaseName("idx_org_admins_user");
    }
}

/// <summary>Maps <see cref="StaffInvite"/> (rc.12).</summary>
internal sealed class StaffInviteConfiguration : IEntityTypeConfiguration<StaffInvite>
{
    public void Configure(EntityTypeBuilder<StaffInvite> b)
    {
        b.ToTable("staff_invites");
        b.HasKey(i => i.Id);
        b.Property(i => i.FirstName).HasMaxLength(100).IsRequired();
        b.Property(i => i.LastName).HasMaxLength(100).IsRequired();
        b.Property(i => i.Email).HasMaxLength(256).IsRequired();
        b.Property(i => i.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(i => i.Mobile).HasMaxLength(20);
        b.Property(i => i.AcceptedDifferences).HasColumnType("jsonb").IsRequired();
        b.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
        b.Property(i => i.CodeHash).HasMaxLength(500).IsRequired();
        b.HasOne<App>().WithMany().HasForeignKey(i => i.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName("ux_staff_invites_token");
        b.HasIndex(i => i.NormalizedEmail).HasDatabaseName("idx_staff_invites_email");
    }
}

/// <summary>Maps <see cref="PendingMembership"/> (rc.12).</summary>
internal sealed class PendingMembershipConfiguration : IEntityTypeConfiguration<PendingMembership>
{
    public void Configure(EntityTypeBuilder<PendingMembership> b)
    {
        b.ToTable("pending_memberships");
        b.HasKey(p => p.Id);
        b.Property(p => p.Roles).HasColumnType("jsonb").IsRequired();
        b.HasOne<StaffInvite>().WithMany().HasForeignKey(p => p.InviteId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organisation>().WithMany().HasForeignKey(p => p.OrgId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<App>().WithMany().HasForeignKey(p => p.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(p => p.InviteId).HasDatabaseName("idx_pending_memberships_invite");
        b.HasIndex(p => new { p.UserId, p.StartsOn }).HasFilter("applied_at IS NULL AND cancelled_at IS NULL").HasDatabaseName("idx_pending_memberships_waiting");
    }
}

/// <summary>Maps <see cref="JoinCode"/> (rc.12).</summary>
internal sealed class JoinCodeConfiguration : IEntityTypeConfiguration<JoinCode>
{
    public void Configure(EntityTypeBuilder<JoinCode> b)
    {
        b.ToTable("join_codes");
        b.HasKey(j => j.Id);
        b.Property(j => j.CodeHash).HasMaxLength(64).IsRequired();
        b.HasOne<SangamUser>().WithMany().HasForeignKey(j => j.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(j => j.CodeHash).HasFilter("used_at IS NULL").HasDatabaseName("idx_join_codes_live");
    }
}

/// <summary>Maps <see cref="StaffCheck"/> (rc.12).</summary>
internal sealed class StaffCheckConfiguration : IEntityTypeConfiguration<StaffCheck>
{
    public void Configure(EntityTypeBuilder<StaffCheck> b)
    {
        b.ToTable("staff_checks");
        b.HasKey(c => c.Id);
        b.Property(c => c.SubjectHash).HasMaxLength(64).IsRequired();
        b.Property(c => c.Outcome).HasMaxLength(20).IsRequired();
        b.Property(c => c.Differences).HasColumnType("jsonb").IsRequired();
        b.HasIndex(c => new { c.AdminUserId, c.SubjectHash, c.CheckedAt }).HasDatabaseName("idx_staff_checks_admin");
    }
}

/// <summary>Maps <see cref="SharedDevice"/> (rc.12, T1).</summary>
internal sealed class SharedDeviceConfiguration : IEntityTypeConfiguration<SharedDevice>
{
    public void Configure(EntityTypeBuilder<SharedDevice> b)
    {
        b.ToTable("shared_devices");
        b.HasKey(d => d.Id);
        b.Property(d => d.Label).HasMaxLength(100).IsRequired();
        b.Property(d => d.KeyHash).HasMaxLength(64);
        b.Property(d => d.EnrolTokenHash).HasMaxLength(64);
        b.HasOne<App>().WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organisation>().WithMany().HasForeignKey(d => d.OrgId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(d => d.KeyHash).IsUnique().HasFilter("key_hash IS NOT NULL").HasDatabaseName("ux_shared_devices_key");
        b.HasIndex(d => d.EnrolTokenHash).IsUnique().HasFilter("enrol_token_hash IS NOT NULL").HasDatabaseName("ux_shared_devices_enrol");
        b.HasIndex(d => new { d.AppId, d.OrgId }).HasDatabaseName("idx_shared_devices_org");
    }
}

/// <summary>Maps <see cref="SharedDevicePerson"/> (rc.12, T1).</summary>
internal sealed class SharedDevicePersonConfiguration : IEntityTypeConfiguration<SharedDevicePerson>
{
    public void Configure(EntityTypeBuilder<SharedDevicePerson> b)
    {
        b.ToTable("shared_device_people");
        b.HasKey(p => p.Id);
        b.Property(p => p.Methods).HasMaxLength(100).IsRequired();
        b.Property(p => p.SignInMode).HasConversion(new SnakeCaseEnumConverter<SignInMode>()).HasMaxLength(20).IsRequired();
        b.HasOne<SharedDevice>().WithMany().HasForeignKey(p => p.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SangamUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(p => new { p.DeviceId, p.UserId }).IsUnique().HasDatabaseName("ux_shared_device_people");
        b.HasIndex(p => new { p.DeviceId, p.LastSeenAt }).IsDescending(false, true).HasDatabaseName("idx_shared_device_people_recent");
    }
}

/// <summary>Maps <see cref="UserPin"/> (rc.12, T1).</summary>
internal sealed class UserPinConfiguration : IEntityTypeConfiguration<UserPin>
{
    public void Configure(EntityTypeBuilder<UserPin> b)
    {
        b.ToTable("user_pins");
        b.HasKey(p => p.UserId);
        b.Property(p => p.PinHash).HasMaxLength(500).IsRequired();
        b.HasOne<SangamUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps <see cref="EmergencyAccess"/> (rc.12, M12).</summary>
internal sealed class EmergencyAccessConfiguration : IEntityTypeConfiguration<EmergencyAccess>
{
    public void Configure(EntityTypeBuilder<EmergencyAccess> b)
    {
        b.ToTable("emergency_accesses");
        b.HasKey(e => e.Id);
        b.Property(e => e.Reason).HasMaxLength(500).IsRequired();
        b.Property(e => e.CaseRef).HasMaxLength(64).IsRequired();
        b.Property(e => e.Method).HasMaxLength(20).IsRequired();
        b.Property(e => e.EndReason).HasMaxLength(500);
        b.Property(e => e.ReviewNote).HasMaxLength(1000);
        b.Property(e => e.Verdict).HasConversion(new SnakeCaseEnumConverter<EmergencyVerdict>()).HasMaxLength(20);
        b.HasOne<App>().WithMany().HasForeignKey(e => e.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organisation>().WithMany().HasForeignKey(e => e.OrgId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SangamUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.UserId, e.StartedAt }).HasDatabaseName("idx_emergency_accesses_user");
        b.HasIndex(e => new { e.OrgId, e.StartedAt }).HasDatabaseName("idx_emergency_accesses_org");
    }
}
