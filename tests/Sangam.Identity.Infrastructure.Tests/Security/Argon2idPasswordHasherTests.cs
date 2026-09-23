using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Security;

namespace Sangam.Identity.Infrastructure.Tests.Security;

public sealed class Argon2idPasswordHasherTests
{
    // Small parameters keep the suite fast; production defaults are m=65536,t=3,p=1.
    private static readonly Argon2idOptions Fast = new() { MemoryKiB = 8192, Iterations = 2, Parallelism = 1 };
    private static readonly SangamUser User = new() { Id = Guid.NewGuid(), Email = "test@example.in" };

    [Fact]
    public void HashPassword_ProducesPhcFormattedArgon2idString()
    {
        Argon2idPasswordHasher<SangamUser> hasher = new(Fast);

        string hash = hasher.HashPassword(User, "correct horse battery staple");

        Assert.StartsWith("$argon2id$v=19$m=8192,t=2,p=1$", hash, StringComparison.Ordinal);
        Assert.Equal(5, hash.Split('$', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("correct", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HashPassword_UsesFreshSaltEveryTime()
    {
        Argon2idPasswordHasher<SangamUser> hasher = new(Fast);

        string first = hasher.HashPassword(User, "same password");
        string second = hasher.HashPassword(User, "same password");

        Assert.NotEqual(first, second);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(User, first, "same password"));
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(User, second, "same password"));
    }

    [Fact]
    public void VerifyHashedPassword_RejectsWrongPassword()
    {
        Argon2idPasswordHasher<SangamUser> hasher = new(Fast);
        string hash = hasher.HashPassword(User, "right");

        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(User, hash, "wrong"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(User, hash, "Right"));
    }

    [Fact]
    public void VerifyHashedPassword_RejectsForeignOrMalformedHashes()
    {
        Argon2idPasswordHasher<SangamUser> hasher = new(Fast);

        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(User, "AQAAAAIAAYagAAAAE", "x"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(User, "$argon2id$v=19$m=8192,t=2,p=1$notbase64!$zzz", "x"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(User, "$argon2id$v=19$m=0,t=2,p=1$YWJj$YWJj", "x"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(User, string.Empty, "x"));
    }

    [Fact]
    public void VerifyHashedPassword_FlagsWeakerParametersForRehash()
    {
        Argon2idPasswordHasher<SangamUser> weak = new(new Argon2idOptions { MemoryKiB = 4096, Iterations = 1, Parallelism = 1 });
        Argon2idPasswordHasher<SangamUser> current = new(Fast);
        string oldHash = weak.HashPassword(User, "pw");

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, current.VerifyHashedPassword(User, oldHash, "pw"));
        Assert.Equal(PasswordVerificationResult.Failed, current.VerifyHashedPassword(User, oldHash, "pw2"));
    }
}
