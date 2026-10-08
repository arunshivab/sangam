using System.Security.Claims;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authorization;
using Sangam.Shared.Constants;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>PR-25 and PR-26 claims: custom claims only under <c>attributes</c>, the verified flag only under <c>profile</c>.</summary>
public sealed class ClaimsBuilderTests
{
    private static readonly string[] ProfileOnly = [SangamScopes.OpenId, SangamScopes.Profile];
    private static readonly string[] WithAttributes = [SangamScopes.OpenId, SangamScopes.Attributes];
    private static readonly string[] Roles = ["nurse"];

    [Fact]
    public void TheVerifiedFlag_IsReleasedUnderProfile_OnlyWhenVerified()
    {
        ClaimsIdentity verified = SangamClaimsBuilder.Build(User(DateTimeOffset.UtcNow), ProfileOnly, [], "test");
        // A boolean claim, as email_verified: the token carries JSON true.
        Assert.Equal(ClaimValueTypes.Boolean, verified.FindFirst(SangamClaims.IdentityVerified)?.ValueType);
        Assert.True(bool.Parse(verified.FindFirst(SangamClaims.IdentityVerified)!.Value));
        Assert.Null(SangamClaimsBuilder.Build(User(null), ProfileOnly, [], "test").FindFirst(SangamClaims.IdentityVerified));
        Assert.Null(SangamClaimsBuilder.Build(User(DateTimeOffset.UtcNow), WithAttributes, [], "test").FindFirst(SangamClaims.IdentityVerified));
    }

    [Fact]
    public void CustomClaims_AreTyped_AndReleasedOnlyUnderAttributes()
    {
        Dictionary<string, object> custom = new() { ["employee_no"] = "E-1042", ["grade"] = 7m, ["on_call"] = false, ["his_roles"] = Roles };
        ClaimsIdentity identity = SangamClaimsBuilder.Build(User(null), WithAttributes, [], "test", custom: custom);
        Assert.Equal("E-1042", identity.FindFirst("employee_no")?.Value);
        Assert.Equal(ClaimValueTypes.Double, identity.FindFirst("grade")?.ValueType);
        Assert.Equal("false", identity.FindFirst("on_call")?.Value);
        Assert.Equal("[\"nurse\"]", identity.FindFirst("his_roles")?.Value);
        Assert.Null(SangamClaimsBuilder.Build(User(null), ProfileOnly, [], "test", custom: custom).FindFirst("employee_no"));
    }

    private static UserSummary User(DateTimeOffset? verifiedAt) => new(
        Guid.NewGuid(), "Meera", "Iyer", "meera@example.in", true, null, false, new DateOnly(1991, 11, 5), Gender.Female, "en-IN",
        SignInMode.Password, DateTimeOffset.UtcNow, false, DateTimeOffset.UtcNow, "stamp", verifiedAt);
}
