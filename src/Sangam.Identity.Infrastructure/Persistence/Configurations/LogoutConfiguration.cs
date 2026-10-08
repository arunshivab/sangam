using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="SessionApp"/> to <c>session_apps</c> (PR-20).</summary>
internal sealed class SessionAppConfiguration : IEntityTypeConfiguration<SessionApp>
{
    public void Configure(EntityTypeBuilder<SessionApp> b)
    {
        b.ToTable("session_apps");
        b.HasKey(x => new { x.SessionId, x.AppId });
        b.HasOne<UserSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps <see cref="LogoutNotification"/> to <c>logout_notifications</c> (PR-20).</summary>
internal sealed class LogoutNotificationConfiguration : IEntityTypeConfiguration<LogoutNotification>
{
    public void Configure(EntityTypeBuilder<LogoutNotification> b)
    {
        b.ToTable("logout_notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.LastError).HasMaxLength(200);
        b.HasIndex(x => new { x.SentAt, x.NextAttemptAt }).HasDatabaseName("idx_logout_notifications_due");
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}
