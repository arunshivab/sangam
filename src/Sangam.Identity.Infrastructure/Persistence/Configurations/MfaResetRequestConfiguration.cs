using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class MfaResetRequestConfiguration : IEntityTypeConfiguration<MfaResetRequest>
{
    public void Configure(EntityTypeBuilder<MfaResetRequest> b)
    {
        b.ToTable("mfa_reset_requests");
        b.HasKey(r => r.Id);
        b.Ignore(r => r.Pending);
        b.Property(r => r.VerificationMethod).HasMaxLength(40).IsRequired();
        b.Property(r => r.Reference).HasMaxLength(200).IsRequired();
        b.Property(r => r.CancelTokenHash).HasMaxLength(64).IsRequired();
        b.Property(r => r.CancelledBy).HasMaxLength(20);
        b.Property(r => r.UrgentReason).HasMaxLength(500);
        b.Property(r => r.ReviewStatus).HasMaxLength(20);
        b.Property(r => r.Record).HasColumnType("jsonb");
        b.Property(r => r.ReviewReason).HasMaxLength(500);
        b.ToTable(t => t.HasCheckConstraint("chk_mfa_reset_requests_review_status", "review_status IS NULL OR review_status IN ('waiting', 'approved', 'refused')"));
        b.HasIndex(r => r.RequestedAt).HasFilter("review_status = 'waiting' AND cancelled_at IS NULL AND applied_at IS NULL").HasDatabaseName("idx_mfa_reset_requests_review");
        b.HasOne<SangamUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => r.UserId).HasDatabaseName("idx_mfa_reset_requests_user");
        b.HasIndex(r => r.CancelTokenHash).IsUnique().HasDatabaseName("ux_mfa_reset_requests_cancel_token");
        b.HasIndex(r => r.EffectiveAt).HasFilter("cancelled_at IS NULL AND applied_at IS NULL").HasDatabaseName("idx_mfa_reset_requests_due");
    }
}
