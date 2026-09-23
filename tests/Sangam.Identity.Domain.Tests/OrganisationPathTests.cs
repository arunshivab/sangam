using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Domain.Tests;

public sealed class OrganisationPathTests
{
    private static readonly Guid Corp = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Hospital = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Department = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ForRoot_WrapsIdInSeparators()
    {
        Assert.Equal("/11111111-1111-1111-1111-111111111111/", OrganisationPath.ForRoot(Corp));
    }

    [Fact]
    public void ForChild_AppendsToParentPath()
    {
        string corp = OrganisationPath.ForRoot(Corp);
        string hospital = OrganisationPath.ForChild(corp, Hospital);
        string department = OrganisationPath.ForChild(hospital, Department);

        Assert.Equal("/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/33333333-3333-3333-3333-333333333333/", department);
        Assert.Equal([Corp, Hospital, Department], OrganisationPath.Ids(department));
    }

    [Fact]
    public void ForChild_RejectsParentPathWithoutTrailingSeparator()
    {
        Assert.Throws<ArgumentException>(() => OrganisationPath.ForChild("/abc", Hospital));
    }

    [Fact]
    public void IsSelfOrDescendant_MatchesSubtreeOnly()
    {
        string corp = OrganisationPath.ForRoot(Corp);
        string hospital = OrganisationPath.ForChild(corp, Hospital);
        string otherRoot = OrganisationPath.ForRoot(Department);

        Assert.True(OrganisationPath.IsSelfOrDescendant(corp, corp));
        Assert.True(OrganisationPath.IsSelfOrDescendant(corp, hospital));
        Assert.False(OrganisationPath.IsSelfOrDescendant(hospital, corp));
        Assert.False(OrganisationPath.IsSelfOrDescendant(corp, otherRoot));
    }
}
