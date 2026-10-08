using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="CustomisationSetting"/> to <c>customisations</c> (PR-19, SGM-209 §8).</summary>
internal sealed class CustomisationConfiguration : IEntityTypeConfiguration<CustomisationSetting>
{
    public void Configure(EntityTypeBuilder<CustomisationSetting> b)
    {
        b.ToTable("customisations", t => t.HasCheckConstraint("chk_customisations_scope", $"scope IN ({SnakeCaseEnumConverter<CustomisationScope>.SqlList()})"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Scope).HasConversion(new SnakeCaseEnumConverter<CustomisationScope>()).HasMaxLength(20).IsRequired();
        b.Property(x => x.Key).HasMaxLength(100).IsRequired();
        b.Property(x => x.Value).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => new { x.Scope, x.ScopeId, x.Key }).IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_customisations_scope_key");
    }
}

/// <summary>Maps <see cref="BrandingAsset"/> to <c>branding_assets</c> (PR-19).</summary>
internal sealed class BrandingAssetConfiguration : IEntityTypeConfiguration<BrandingAsset>
{
    public void Configure(EntityTypeBuilder<BrandingAsset> b)
    {
        b.ToTable("branding_assets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Scope).HasConversion(new SnakeCaseEnumConverter<CustomisationScope>()).HasMaxLength(20).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(40).IsRequired();
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.Scope, x.ScopeId }).HasDatabaseName("idx_branding_assets_scope");
    }
}

/// <summary>Maps <see cref="MessageTemplate"/> to <c>message_templates</c> (PR-19, SGM-209 §8).</summary>
internal sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> b)
    {
        b.ToTable("message_templates", t => t.HasCheckConstraint("chk_message_templates_scope", $"scope IN ({SnakeCaseEnumConverter<CustomisationScope>.SqlList()})"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Scope).HasConversion(new SnakeCaseEnumConverter<CustomisationScope>()).HasMaxLength(20).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(50).IsRequired();
        b.Property(x => x.Language).HasMaxLength(10).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.Body).HasMaxLength(8000).IsRequired();
        b.Property(x => x.DltTemplateId).HasMaxLength(30);
        b.HasIndex(x => new { x.Scope, x.ScopeId, x.Kind, x.Language }).IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_message_templates_scope_kind_language");
    }
}
