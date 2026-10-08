using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class EmailChangeRequestConfiguration : IEntityTypeConfiguration<EmailChangeRequest>
{
    public void Configure(EntityTypeBuilder<EmailChangeRequest> b)
    {
        b.ToTable("email_change_requests");
        b.HasKey(r => r.Id);
        b.Property(r => r.NewEmail).HasMaxLength(256).IsRequired();
        b.Property(r => r.NormalizedNewEmail).HasMaxLength(256).IsRequired();
        b.HasOne<SangamUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => r.UserId).HasDatabaseName("idx_email_change_requests_user");
    }
}
