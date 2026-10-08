using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class PasskeyCredentialConfiguration : IEntityTypeConfiguration<PasskeyCredential>
{
    public void Configure(EntityTypeBuilder<PasskeyCredential> b)
    {
        b.ToTable("passkey_credentials");
        b.HasKey(c => c.Id);
        b.Property(c => c.CredentialId).IsRequired();
        b.Property(c => c.Name).HasMaxLength(60).IsRequired();
        b.HasIndex(c => c.CredentialId).IsUnique().HasDatabaseName("idx_passkey_credentials_credential_id");
        b.HasIndex(c => c.UserId).HasDatabaseName("idx_passkey_credentials_user");
        b.HasOne<SangamUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>D-G: ASP.NET Core Identity's own passkey store (schema version 3), in Sangam's naming.</summary>
internal sealed class IdentityUserPasskeyConfiguration : IEntityTypeConfiguration<Microsoft.AspNetCore.Identity.IdentityUserPasskey<Guid>>
{
    public void Configure(EntityTypeBuilder<Microsoft.AspNetCore.Identity.IdentityUserPasskey<Guid>> b) => b.ToTable("user_passkeys");
}

internal sealed class PasskeyChallengeConfiguration : IEntityTypeConfiguration<PasskeyChallenge>
{
    public void Configure(EntityTypeBuilder<PasskeyChallenge> b)
    {
        b.ToTable("passkey_challenges");
        b.HasKey(c => c.Id);
        b.Property(c => c.Kind).HasMaxLength(10).IsRequired();
        b.Property(c => c.OptionsJson).IsRequired();
        b.HasIndex(c => c.ExpiresAt).HasDatabaseName("idx_passkey_challenges_expires");
    }
}
