using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class AccountMergeRequestConfiguration : IEntityTypeConfiguration<AccountMergeRequest>
{
    public void Configure(EntityTypeBuilder<AccountMergeRequest> b)
    {
        b.ToTable("account_merge_requests");
        b.HasKey(r => r.Id);
        b.Property(r => r.CancelTokenHash).HasMaxLength(64).IsRequired();
        b.Property(r => r.CancelledBy).HasMaxLength(20);
        b.ToTable(t => t.HasCheckConstraint("chk_account_merge_requests_two_accounts", "survivor_user_id <> closed_user_id"));

        // Both accounts stay as rows (a closed account is pseudonymised, never deleted), so the links hold.
        b.HasOne<SangamUser>().WithMany().HasForeignKey(r => r.SurvivorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SangamUser>().WithMany().HasForeignKey(r => r.ClosedUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(r => r.SurvivorUserId).HasDatabaseName("idx_account_merge_requests_survivor");
        b.HasIndex(r => r.ClosedUserId).HasDatabaseName("idx_account_merge_requests_closed");
        b.HasIndex(r => r.CancelTokenHash).IsUnique().HasDatabaseName("ux_account_merge_requests_cancel_token");
        b.HasIndex(r => r.EffectiveAt).HasFilter("cancelled_at IS NULL AND applied_at IS NULL").HasDatabaseName("idx_account_merge_requests_due");
    }
}
