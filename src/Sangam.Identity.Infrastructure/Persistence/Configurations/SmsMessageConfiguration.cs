using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="SmsMessage"/> to <c>sms_messages</c> (SGM-206 §5).</summary>
internal sealed class SmsMessageConfiguration : IEntityTypeConfiguration<SmsMessage>
{
    public void Configure(EntityTypeBuilder<SmsMessage> b)
    {
        b.ToTable("sms_messages");
        b.HasKey(m => m.Id);
        b.Property(m => m.ToHash).HasMaxLength(64).IsRequired();
        b.Property(m => m.IpHash).HasMaxLength(64);
        b.Property(m => m.Template).HasMaxLength(40).IsRequired();
        b.Property(m => m.Provider).HasMaxLength(40).IsRequired();
        b.Property(m => m.ProviderMessageId).HasMaxLength(100);
        b.Property(m => m.Status).HasConversion(new SnakeCaseEnumConverter<SmsStatus>()).HasMaxLength(20).IsRequired();
        b.Property(m => m.Error).HasMaxLength(200);
        b.HasIndex(m => new { m.ToHash, m.CreatedAt }).HasDatabaseName("idx_sms_messages_to_created");
        b.HasIndex(m => new { m.IpHash, m.CreatedAt }).HasDatabaseName("idx_sms_messages_ip_created");
        b.HasIndex(m => new { m.Provider, m.ProviderMessageId }).HasDatabaseName("idx_sms_messages_provider_message");
        b.HasIndex(m => m.CreatedAt).HasDatabaseName("idx_sms_messages_created");
        // The row outlives a hard-deleted account, without the link, so volume and limits stay accountable.
        b.HasOne<SangamUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}
