using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> b)
    {
        b.ToTable("invitations");
        b.HasKey(i => i.Id);
        b.Property(i => i.RoleCode).HasMaxLength(64).IsRequired();
        b.Property(i => i.Email).HasMaxLength(256).IsRequired();
        b.Property(i => i.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName("idx_invitations_token_hash");
        b.HasIndex(i => new { i.AppId, i.OrgId }).HasDatabaseName("idx_invitations_app_org");
        b.HasOne<App>().WithMany().HasForeignKey(i => i.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organisation>().WithMany().HasForeignKey(i => i.OrgId).OnDelete(DeleteBehavior.Cascade);
    }
}
