using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

public sealed class SnakeCaseEnumConverterTests
{
    [Fact]
    public void ToSnake_MatchesTheDesignDocumentValues()
    {
        Assert.Equal("active", SnakeCaseEnumConverter<UserStatus>.ToSnake(UserStatus.Active));
        Assert.Equal("deleted_soft", SnakeCaseEnumConverter<UserStatus>.ToSnake(UserStatus.DeletedSoft));
        Assert.Equal("deleted_hard", SnakeCaseEnumConverter<UserStatus>.ToSnake(UserStatus.DeletedHard));
        Assert.Equal("anonymous", SnakeCaseEnumConverter<AuditActorType>.ToSnake(AuditActorType.Anonymous));
    }

    [Fact]
    public void RoundTrips_EveryValue()
    {
        SnakeCaseEnumConverter<UserStatus> converter = new();
        foreach (UserStatus value in Enum.GetValues<UserStatus>())
        {
            string stored = (string)converter.ConvertToProvider(value)!;
            Assert.Equal(value, (UserStatus)converter.ConvertFromProvider(stored)!);
        }
    }

    [Fact]
    public void SqlList_IsQuotedAndCommaSeparated()
    {
        Assert.Equal("'active','disabled'", SnakeCaseEnumConverter<AppStatus>.SqlList());
    }
}
