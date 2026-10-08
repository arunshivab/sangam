using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class UserAttributeDefinitionConfiguration : IEntityTypeConfiguration<UserAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<UserAttributeDefinition> b)
    {
        b.ToTable("user_attribute_definitions", t =>
        {
            t.HasCheckConstraint("chk_user_attribute_definitions_type", "type IN ('text','number','date','boolean','choice')");
            t.HasCheckConstraint("chk_user_attribute_definitions_editable", "editable_by IN ('admin','person')");
        });
        b.HasKey(d => d.Id);
        b.Property(d => d.Key).HasMaxLength(40).IsRequired();
        b.Property(d => d.Label).HasMaxLength(80).IsRequired();
        b.Property(d => d.Type).HasMaxLength(10);
        b.Property(d => d.Choices).HasMaxLength(500);
        b.Property(d => d.EditableBy).HasMaxLength(10);
        b.HasIndex(d => new { d.AppId, d.OrgId, d.Key }).IsUnique().HasFilter("retired_at IS NULL").AreNullsDistinct(false).HasDatabaseName("ux_user_attribute_definitions_live");
        b.HasOne<App>().WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organisation>().WithMany().HasForeignKey(d => d.OrgId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserAttributeValueConfiguration : IEntityTypeConfiguration<UserAttributeValue>
{
    public void Configure(EntityTypeBuilder<UserAttributeValue> b)
    {
        b.ToTable("user_attribute_values");
        b.HasKey(v => new { v.DefinitionId, v.UserId });
        b.Property(v => v.Value).HasMaxLength(200).IsRequired();
        b.HasIndex(v => v.UserId).HasDatabaseName("idx_user_attribute_values_user");
        b.HasOne<UserAttributeDefinition>().WithMany().HasForeignKey(v => v.DefinitionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SangamUser>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AppClaimMappingConfiguration : IEntityTypeConfiguration<AppClaimMapping>
{
    public void Configure(EntityTypeBuilder<AppClaimMapping> b)
    {
        b.ToTable("app_claim_mappings", t => t.HasCheckConstraint("chk_app_claim_mappings_source", "source IN ('attribute','roles','permissions','org_names')"));
        b.HasKey(m => m.Id);
        b.Property(m => m.ClaimName).HasMaxLength(40).IsRequired();
        b.Property(m => m.Source).HasMaxLength(20);
        b.Property(m => m.AttributeKey).HasMaxLength(40);
        b.HasIndex(m => new { m.AppId, m.ClaimName }).IsUnique().HasDatabaseName("ux_app_claim_mappings_name");
        b.HasOne<App>().WithMany().HasForeignKey(m => m.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}
