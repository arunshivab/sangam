using Sangam.Identity.Application.Apps;

namespace Sangam.Identity.Application.Tests.Apps;

/// <summary>rc.14 (ADR-0017): every application has its own <c>sid</c> for a sign-in session; Sangam's own keep the session id.</summary>
public sealed class AppSessionIdTests
{
    [Fact]
    public void EachApplication_GetsItsOwnSid_TheSameEveryTime()
    {
        Guid session = Guid.NewGuid();
        Guid his = Guid.NewGuid();
        Guid lab = Guid.NewGuid();

        Guid forHis = AppSessionIds.For(session, his, platform: false);
        Assert.Equal(forHis, AppSessionIds.For(session, his, platform: false));
        Assert.NotEqual(forHis, AppSessionIds.For(session, lab, platform: false));
        Assert.NotEqual(session, forHis);
        Assert.NotEqual(forHis, AppSessionIds.For(Guid.NewGuid(), his, platform: false));

        // It reads as an ordinary UUID (version 8, RFC 9562 variant).
        string text = forHis.ToString("D");
        Assert.Equal('8', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void SangamsOwnApplications_KeepTheSessionId()
    {
        Guid session = Guid.NewGuid();
        Assert.Equal(session, AppSessionIds.For(session, Guid.NewGuid(), platform: true));
    }
}
