using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class AppEventConfiguration : IEntityTypeConfiguration<AppEvent>
{
    public void Configure(EntityTypeBuilder<AppEvent> b)
    {
        b.ToTable("app_events");
        b.HasKey(e => e.Sequence);
        b.Property(e => e.Sequence).UseIdentityAlwaysColumn();
        b.HasIndex(e => e.Id).IsUnique().HasDatabaseName("ux_app_events_id");
        b.Property(e => e.Type).HasMaxLength(40).IsRequired();
        b.Property(e => e.Data).HasColumnType("jsonb").IsRequired();
        b.HasIndex(e => e.Sequence).HasFilter("dispatched_at IS NULL").HasDatabaseName("idx_app_events_pending");
        b.HasIndex(e => new { e.AppId, e.CreatedAt }).HasDatabaseName("idx_app_events_app_created");
        b.HasOne<App>().WithMany().HasForeignKey(e => e.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScimTargetConfiguration : IEntityTypeConfiguration<ScimTarget>
{
    public void Configure(EntityTypeBuilder<ScimTarget> b)
    {
        b.ToTable("scim_targets", t =>
        {
            t.HasCheckConstraint("chk_scim_targets_auth", "auth_mode IN ('bearer','sangam')");
            t.HasCheckConstraint("chk_scim_targets_mapping", "group_mapping IN ('role','role_org')");
            t.HasCheckConstraint("chk_scim_targets_status", "status IN ('ok','failing')");
        });
        b.HasKey(t => t.AppId);
        b.Property(t => t.BaseUrl).HasMaxLength(500).IsRequired();
        b.Property(t => t.AuthMode).HasMaxLength(10);
        b.Property(t => t.ProtectedToken).HasMaxLength(4000);
        b.Property(t => t.GroupMapping).HasMaxLength(10);
        b.Property(t => t.Status).HasMaxLength(10);
        b.Property(t => t.LastReconcileSummary).HasMaxLength(500);
        b.HasOne<App>().WithOne().HasForeignKey<ScimTarget>(t => t.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScimUserLinkConfiguration : IEntityTypeConfiguration<ScimUserLink>
{
    public void Configure(EntityTypeBuilder<ScimUserLink> b)
    {
        b.ToTable("scim_user_links");
        b.HasKey(l => new { l.AppId, l.UserId });
        b.Property(l => l.RemoteId).HasMaxLength(200).IsRequired();
        b.HasOne<App>().WithMany().HasForeignKey(l => l.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScimGroupLinkConfiguration : IEntityTypeConfiguration<ScimGroupLink>
{
    public void Configure(EntityTypeBuilder<ScimGroupLink> b)
    {
        b.ToTable("scim_group_links");
        b.HasKey(l => new { l.AppId, l.GroupKey });
        b.Property(l => l.GroupKey).HasMaxLength(120);
        b.Property(l => l.RemoteId).HasMaxLength(200).IsRequired();
        b.Property(l => l.DisplayName).HasMaxLength(300).IsRequired();
        b.HasOne<App>().WithMany().HasForeignKey(l => l.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScimGroupMemberConfiguration : IEntityTypeConfiguration<ScimGroupMember>
{
    public void Configure(EntityTypeBuilder<ScimGroupMember> b)
    {
        b.ToTable("scim_group_members");
        b.HasKey(m => new { m.AppId, m.GroupKey, m.UserId });
        b.Property(m => m.GroupKey).HasMaxLength(120);
        b.HasOne<App>().WithMany().HasForeignKey(m => m.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScimDeliveryConfiguration : IEntityTypeConfiguration<ScimDelivery>
{
    public void Configure(EntityTypeBuilder<ScimDelivery> b)
    {
        b.ToTable("scim_deliveries", t => t.HasCheckConstraint("chk_scim_deliveries_status", "status IN ('pending','done','dead')"));
        b.HasKey(d => d.Id);
        b.Property(d => d.Id).UseIdentityAlwaysColumn();
        b.Property(d => d.Reason).HasMaxLength(40);
        b.Property(d => d.Status).HasMaxLength(10);
        b.Property(d => d.Summary).HasMaxLength(1000);
        b.Property(d => d.LastError).HasMaxLength(1000);
        b.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'pending'").HasDatabaseName("idx_scim_deliveries_due");
        b.HasIndex(d => new { d.AppId, d.CreatedAt }).HasDatabaseName("idx_scim_deliveries_app_created");
        b.HasOne<App>().WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}
