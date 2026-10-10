using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Security;

namespace Sangam.Identity.Infrastructure.Tests.Security;

public sealed class Argon2idPasswordHasherTests
{
    // Small parameters keep the suite fast; production defaults are m=65536,t=3,p=1.
    private static readonly Argon2idOptions Fast = new() { MemoryKiB = 8192, Iterations = 2, Parallelism = 1 };
    private static readonly SangamUser User = new() { Id = Guid.NewGuid(), Email = "test@example.in" };
    private static readonly string First = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string Second = Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());

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

    [Fact]
    public void WithAPepper_TheHashNamesItsVersion_AndDoesNotVerifyWithoutThePepper()
    {
        // rc.5 (ASVS V2.4.5): a copy of the database alone reveals nothing.
        Argon2idPasswordHasher<SangamUser> peppered = new(Peppered(First, 1));
        string hash = peppered.HashPassword(User, "monsoon tea at baner");

        Assert.True(peppered.Peppered);
        Assert.Contains(",keyid=1$", hash, StringComparison.Ordinal);
        Assert.Equal(PasswordVerificationResult.Success, peppered.VerifyHashedPassword(User, hash, "monsoon tea at baner"));
        Assert.Equal(PasswordVerificationResult.Failed, new Argon2idPasswordHasher<SangamUser>(Fast).VerifyHashedPassword(User, hash, "monsoon tea at baner"));
        Assert.Equal(PasswordVerificationResult.Failed, new Argon2idPasswordHasher<SangamUser>(Peppered(Second, 1)).VerifyHashedPassword(User, hash, "monsoon tea at baner"));
    }

    [Fact]
    public void AnUnpepperedHash_StillVerifies_AndIsRehashedWithThePepper()
    {
        string old = new Argon2idPasswordHasher<SangamUser>(Fast).HashPassword(User, "monsoon tea at baner");
        Argon2idPasswordHasher<SangamUser> peppered = new(Peppered(First, 1));

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, peppered.VerifyHashedPassword(User, old, "monsoon tea at baner"));
        Assert.Equal(PasswordVerificationResult.Failed, peppered.VerifyHashedPassword(User, old, "another phrase"));
    }

    [Fact]
    public void AReplacedPepper_KeptAsPrevious_StillVerifies_AndMovesToTheNewOne()
    {
        string before = new Argon2idPasswordHasher<SangamUser>(Peppered(First, 1)).HashPassword(User, "monsoon tea at baner");
        Argon2idOptions rotated = Peppered(Second, 2);
        rotated.PreviousPeppers[1] = First;
        Argon2idPasswordHasher<SangamUser> hasher = new(rotated);

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, hasher.VerifyHashedPassword(User, before, "monsoon tea at baner"));
        string after = hasher.HashPassword(User, "monsoon tea at baner");
        Assert.Contains(",keyid=2$", after, StringComparison.Ordinal);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(User, after, "monsoon tea at baner"));
        Assert.Equal(PasswordVerificationResult.Failed, new Argon2idPasswordHasher<SangamUser>(Peppered(Second, 2)).VerifyHashedPassword(User, before, "monsoon tea at baner"));
    }

    [Fact]
    public void APepperShorterThan32Bytes_IsNotUsed()
    {
        Argon2idPasswordHasher<SangamUser> hasher = new(Peppered(Convert.ToBase64String(new byte[16]), 1));
        Assert.False(hasher.Peppered);
        Assert.DoesNotContain("keyid", hasher.HashPassword(User, "monsoon tea at baner"), StringComparison.Ordinal);
    }

    private static Argon2idOptions Peppered(string pepper, int version)
        => new() { MemoryKiB = Fast.MemoryKiB, Iterations = Fast.Iterations, Parallelism = Fast.Parallelism, Pepper = pepper, PepperVersion = version };
}
