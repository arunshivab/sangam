using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Consent"/>.</summary>
internal sealed class ConsentConfiguration : IEntityTypeConfiguration<Consent>
{
    public void Configure(EntityTypeBuilder<Consent> b)
    {
        b.ToTable("consents");
        b.HasKey(c => c.Id);
        b.Property(c => c.Scope).HasMaxLength(500).IsRequired();
        b.Property(c => c.ConsentVersion).HasMaxLength(20).IsRequired();
        b.Property(c => c.IpAddress).HasMaxLength(45);
        b.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.App).WithMany().HasForeignKey(c => c.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(c => new { c.UserId, c.AppId }).HasFilter("revoked_at IS NULL").HasDatabaseName("idx_consents_user_app");
    }
}
