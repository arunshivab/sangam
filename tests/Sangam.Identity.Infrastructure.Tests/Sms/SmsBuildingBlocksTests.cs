using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Infrastructure.Tests.Sms;

/// <summary>PR-15: DLT template rendering, masking and hashing, failover and the production start-up rule.</summary>
public sealed class SmsBuildingBlocksTests
{
    private static readonly OutgoingSms Message = new("+919000000061", "sign_in", "1107000000000000001", "SANGAM", "123456 is your code");

    [Fact]
    public void Templates_FillEachVariableInOrder_ExactlyAsRegistered()
    {
        string text = SmsSettings.DefaultTexts[SmsSettings.StepUpTemplate];
        Assert.Equal(2, DltTemplate.VariableCount(text));
        Assert.Equal("482913 is your SangamID code to confirm an action in Lipi HIS. - SANGAM", DltTemplate.Render(text, "482913", "Lipi HIS"));
        Assert.Throws<ArgumentException>(() => DltTemplate.Render(text, "482913"));
        Assert.Throws<ArgumentException>(() => DltTemplate.Render(text, "482913", "line\nbreak"));
        Assert.Throws<ArgumentException>(() => DltTemplate.Render(text, "482913", new string('x', 31)));
    }

    [Fact]
    public void Numbers_AreMaskedForScreens_AndHashedWithAKey()
    {
        Assert.Equal("+91 ••••••10", SmsNumbers.Mask("+919876543210"));
        Assert.Equal("+91 ••••••3210", SmsNumbers.Mask("+919876543210", 4));
        Assert.Equal("•••", SmsNumbers.Mask(null));
        string a = SmsNumbers.Hash("key-one", "+919876543210");
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, SmsNumbers.Hash("key-two", "+919876543210"));
        Assert.Equal(a, SmsNumbers.Hash("key-one", "+919876543210"));
    }

    [Fact]
    public async Task Failover_TriesTheNextProvider_WhenOneRefusesOrBreaks()
    {
        FailoverSmsSender chain = new([new FakeSender("first", accept: false), new FakeSender("broken", accept: true, throws: true), new FakeSender("second", accept: true)]);
        SmsSendResult result = await chain.SendAsync(Message);
        Assert.True(result.Accepted);
        Assert.Equal("second", result.Provider);

        FailoverSmsSender none = new([new FakeSender("only", accept: false)]);
        SmsSendResult refused = await none.SendAsync(Message);
        Assert.False(refused.Accepted);
        Assert.Equal("only", refused.Provider);
    }

    [Theory]
    [InlineData("outbox", "SANGAM", "a-production-hash-key-of-at-least-32-chars", "1107", "development SMS outbox")]
    [InlineData("acme", "SANGAM", "a-production-hash-key-of-at-least-32-chars", "1107", "has no adapter")]
    public void Production_RefusesUnsafeSms(string provider, string header, string hashKey, string templateId, string expected)
    {
        string? problem = SmsGuard.Validate("Production", Config(provider, header, hashKey, templateId));
        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_StartsWhenSmsIsOff_AndDevelopmentMayUseTheOutbox()
    {
        IConfiguration off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sangam:Sms:Enabled"] = "false" }).Build();
        Assert.Null(SmsGuard.Validate("Production", off));
        Assert.Null(SmsGuard.Validate("Development", Config("outbox", "SANGAM", string.Empty, string.Empty)));
    }

    [Fact]
    public void ATemplateWithoutAPlaceForTheCode_StopsEveryEnvironment()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sangam:Sms:Enabled"] = "true",
            ["Sangam:Sms:Provider"] = "outbox",
            ["Sangam:Sms:Templates:sign_in:Text"] = "Your code is ready. - SANGAM",
        }).Build();
        Assert.Contains("sign_in", SmsGuard.Validate("Development", config), StringComparison.Ordinal);
    }

    private static IConfiguration Config(string provider, string header, string hashKey, string templateId) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Sangam:Sms:Enabled"] = "true",
        ["Sangam:Sms:Provider"] = provider,
        ["Sangam:Sms:SenderHeader"] = header,
        ["Sangam:Sms:HashKey"] = hashKey,
        ["Sangam:Sms:Templates:sign_in:Id"] = templateId,
        ["Sangam:Sms:Templates:mobile_verification:Id"] = templateId,
        ["Sangam:Sms:Templates:step_up:Id"] = templateId,
    }).Build();

    private sealed class FakeSender : ISmsSender
    {
        private readonly bool _accept;
        private readonly bool _throws;

        public FakeSender(string name, bool accept, bool throws = false)
        {
            Name = name;
            _accept = accept;
            _throws = throws;
        }

        public string Name { get; }

        public Task<SmsSendResult> SendAsync(OutgoingSms message, CancellationToken cancellationToken = default)
            => _throws ? throw new HttpRequestException("down") : Task.FromResult(new SmsSendResult(_accept, Name, _accept ? "id-" + Name : null, _accept ? null : "refused"));
    }
}
