using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="AppSubject"/> (rc.14, pairwise subject ids).</summary>
internal sealed class AppSubjectConfiguration : IEntityTypeConfiguration<AppSubject>
{
    public void Configure(EntityTypeBuilder<AppSubject> b)
    {
        b.ToTable("app_subjects");
        b.HasKey(s => new { s.AppId, s.UserId });

        // An application's ids are unique within it; the same person has one id per application.
        b.HasIndex(s => new { s.AppId, s.Subject }).IsUnique().HasDatabaseName("ux_app_subjects_subject");
        b.HasIndex(s => s.UserId).HasDatabaseName("idx_app_subjects_user");

        // They go with the account and with the application.
        b.HasOne<SangamUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<App>().WithMany().HasForeignKey(s => s.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}
