using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="LoginIdRelease"/> (rc.11).</summary>
internal sealed class LoginIdReleaseConfiguration : IEntityTypeConfiguration<LoginIdRelease>
{
    public void Configure(EntityTypeBuilder<LoginIdRelease> b)
    {
        b.ToTable("login_id_releases");
        b.HasKey(r => r.Id);
        b.Property(r => r.LoginId).HasMaxLength(30).IsRequired();
        b.HasIndex(r => new { r.LoginId, r.ReleasedAt }).HasDatabaseName("idx_login_id_releases_login_id");
    }
}
