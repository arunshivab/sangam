using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Tests.Accounts;

internal static class TestUsers
{
    public static SangamUser New(string email, string? mobile, bool verified = true)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SangamUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = verified,
            FirstName = "Test",
            LastName = "User",
            DateOfBirth = new DateOnly(1990, 1, 1),
            Gender = Gender.PreferNotToSay,
            PhoneNumber = mobile,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            LastPasswordChangeAt = now,
        };
    }
}
