using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditEvent"/>. No foreign keys on purpose: audit rows must outlive users and
/// apps, and a hard-deleted user must still be attributable. Append-only rules are added in the
/// migration (<c>audit_no_update</c>, <c>audit_no_delete</c>).
/// </summary>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedOnAdd();
        b.Property(e => e.ActorType).HasConversion(new SnakeCaseEnumConverter<AuditActorType>()).HasMaxLength(20).IsRequired();
        b.Property(e => e.Action).HasMaxLength(100).IsRequired();
        b.Property(e => e.TargetType).HasMaxLength(50);
        b.Property(e => e.Metadata).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.IpAddress).HasMaxLength(45);

        b.HasIndex(e => new { e.ActorUserId, e.OccurredAt }).IsDescending(false, true).HasDatabaseName("idx_audit_actor");
        b.HasIndex(e => new { e.TargetType, e.TargetId, e.OccurredAt }).IsDescending(false, false, true).HasDatabaseName("idx_audit_target");
        b.HasIndex(e => new { e.Action, e.OccurredAt }).IsDescending(false, true).HasDatabaseName("idx_audit_action");
        b.HasIndex(e => e.OccurredAt).IsDescending().HasDatabaseName("idx_audit_occurred_at");
    }
}
