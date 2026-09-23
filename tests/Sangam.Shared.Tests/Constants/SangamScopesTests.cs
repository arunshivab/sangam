using Sangam.Shared.Constants;

namespace Sangam.Shared.Tests.Constants;

public sealed class SangamScopesTests
{
    [Fact]
    public void All_ContainsEveryScopeInCanonicalOrder()
    {
        Assert.Equal(["openid", "profile", "email", "orgs.read"], SangamScopes.All);
    }

    [Fact]
    public void All_ContainsOpenIdScope()
    {
        Assert.Contains(SangamScopes.OpenId, SangamScopes.All);
    }

    [Fact]
    public void ScopeNames_AreLowercaseWireIdentifiers()
    {
        foreach (string scope in SangamScopes.All)
        {
            Assert.Equal(scope.ToLowerInvariant(), scope);
            Assert.DoesNotContain(' ', scope);
        }
    }
}
