using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="SignatureRequest"/> to <c>signature_requests</c> (PR-17).</summary>
internal sealed class SignatureRequestConfiguration : IEntityTypeConfiguration<SignatureRequest>
{
    public void Configure(EntityTypeBuilder<SignatureRequest> b)
    {
        b.ToTable("signature_requests", t => t.HasCheckConstraint("chk_signature_requests_status", $"status IN ({SnakeCaseEnumConverter<SignatureStatus>.SqlList()})"));
        b.HasKey(r => r.Id);
        b.Property(r => r.RecordId).HasMaxLength(200).IsRequired();
        b.Property(r => r.RecordHash).HasMaxLength(140).IsRequired();
        b.Property(r => r.Meaning).HasMaxLength(100).IsRequired();
        b.Property(r => r.DisplayText).HasMaxLength(1000).IsRequired();
        b.Property(r => r.ReturnUrl).HasMaxLength(500).IsRequired();
        b.Property(r => r.Status).HasConversion(new SnakeCaseEnumConverter<SignatureStatus>()).HasMaxLength(20).IsRequired();
        b.Property(r => r.Acr).HasMaxLength(40);
        b.Property(r => r.Amr).HasMaxLength(100);
        b.HasIndex(r => new { r.AppId, r.CreatedAt }).HasDatabaseName("idx_signature_requests_app_created");
        b.HasOne<App>().WithMany().HasForeignKey(r => r.AppId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SangamUser>().WithMany().HasForeignKey(r => r.DecidedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
