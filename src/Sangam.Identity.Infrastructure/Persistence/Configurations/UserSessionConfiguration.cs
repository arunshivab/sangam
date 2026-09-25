using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="UserSession"/>.</summary>
internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("user_sessions");
        b.HasKey(s => s.Id);
        b.Property(s => s.DeviceLabel).HasMaxLength(100);
        b.Property(s => s.IpAddress).HasMaxLength(45);
        b.Property(s => s.UserAgent).HasMaxLength(500);
        b.Property(s => s.RevokedReason).HasMaxLength(30);
        b.Property(s => s.SignInMode).HasConversion(new SnakeCaseEnumConverter<SignInMode>()).HasMaxLength(20).IsRequired();

        b.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(s => s.App).WithMany().HasForeignKey(s => s.AppId).OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(s => new { s.UserId, s.LastSeenAt })
            .IsDescending(false, true)
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("idx_user_sessions_live");
        b.HasIndex(s => s.RevokedAt).HasDatabaseName("idx_user_sessions_revoked_at");
    }
}
