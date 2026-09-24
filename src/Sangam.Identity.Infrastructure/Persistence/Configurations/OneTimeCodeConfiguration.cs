using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OneTimeCode"/>.</summary>
internal sealed class OneTimeCodeConfiguration : IEntityTypeConfiguration<OneTimeCode>
{
    public void Configure(EntityTypeBuilder<OneTimeCode> b)
    {
        b.ToTable("one_time_codes");
        b.HasKey(c => c.Id);
        b.Property(c => c.Purpose).HasConversion(new SnakeCaseEnumConverter<OneTimeCodePurpose>()).HasMaxLength(30).IsRequired();
        b.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();
        b.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(c => new { c.UserId, c.Purpose, c.CreatedAt }).HasDatabaseName("idx_one_time_codes_user_purpose_created");
    }
}
