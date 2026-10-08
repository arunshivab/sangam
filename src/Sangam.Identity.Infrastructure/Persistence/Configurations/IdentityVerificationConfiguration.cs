using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class IdentityVerificationConfiguration : IEntityTypeConfiguration<IdentityVerification>
{
    public void Configure(EntityTypeBuilder<IdentityVerification> b)
    {
        b.ToTable("identity_verifications", t => t.HasCheckConstraint("chk_identity_verifications_method", "method IN ('digilocker')"));
        b.HasKey(v => v.Id);
        b.Property(v => v.Method).HasMaxLength(20);
        b.Property(v => v.SubjectHash).HasMaxLength(64).IsRequired();
        b.Property(v => v.Name).HasMaxLength(200).IsRequired();
        b.Property(v => v.Gender).HasConversion(new SnakeCaseEnumConverter<Gender>()).HasMaxLength(20).IsRequired();
        b.HasIndex(v => v.UserId).IsUnique().HasDatabaseName("ux_identity_verifications_user");
        b.HasIndex(v => new { v.Method, v.SubjectHash }).IsUnique().HasDatabaseName("ux_identity_verifications_subject");
        b.HasOne<SangamUser>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
