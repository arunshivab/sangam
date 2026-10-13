using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="AccessReviewDecision"/> (rc.11, R6).</summary>
internal sealed class AccessReviewDecisionConfiguration : IEntityTypeConfiguration<AccessReviewDecision>
{
    public void Configure(EntityTypeBuilder<AccessReviewDecision> b)
    {
        b.ToTable("access_review_decisions");
        b.HasKey(d => d.Id);
        b.Property(d => d.Reason).HasMaxLength(20).IsRequired();
        b.Property(d => d.Outcome).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.Note).HasMaxLength(300);
        b.HasOne<App>().WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(d => new { d.AppId, d.DecidedAt }).HasDatabaseName("idx_access_review_decisions_app");
        b.HasIndex(d => d.MembershipId).HasDatabaseName("idx_access_review_decisions_membership");
    }
}
