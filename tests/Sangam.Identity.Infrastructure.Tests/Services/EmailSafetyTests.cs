using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Tests.Services;

/// <summary>OI-038: one-time codes never reach a log, and production never runs without a real sender.</summary>
public sealed class EmailSafetyTests
{
    private const string Code = "482913";

    private static readonly EmailMessage Message = new("kaveri.nair@example.com", "Kaveri Nair", $"{Code} is your Sangam verification code", $"Your Sangam verification code is:\n\n    {Code}\n");

    private static IConfiguration Config(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Guard_AllowsTheOutbox_InDevelopmentAndTesting(string environment)
    {
        Assert.Null(EmailSenderGuard.Validate(environment, Config((EmailSenderGuard.OutboxKey, "true"))));
    }

    [Fact]
    public void Guard_RefusesTheOutbox_InProduction()
    {
        string? problem = EmailSenderGuard.Validate("Production", Config((EmailSenderGuard.OutboxKey, "true"), (EmailSenderGuard.SmtpHostKey, "smtp.example")));
        Assert.NotNull(problem);
        Assert.Contains("outbox", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_RefusesProduction_WithoutARealSender()
    {
        string? problem = EmailSenderGuard.Validate("Production", Config());
        Assert.NotNull(problem);
        Assert.Contains(EmailSenderGuard.SmtpHostKey, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_AllowsProduction_WithARealSender()
    {
        Assert.Null(EmailSenderGuard.Validate("Production", Config((EmailSenderGuard.SmtpHostKey, "smtp.example"))));
    }

    [Fact]
    public async Task UnavailableSender_Refuses_WithoutRevealingTheMessage()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => new UnavailableEmailSender().SendAsync(Message));
        Assert.DoesNotContain(Code, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kaveri", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Outbox_LogsNeitherTheCodeNorTheSubjectNorTheFullAddress()
    {
        CapturingLogger<InMemoryEmailOutbox> logger = new();
        await new InMemoryEmailOutbox(logger).SendAsync(Message);

        string logged = Assert.Single(logger.Lines);
        Assert.DoesNotContain(Code, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("verification code", logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kaveri.nair@", logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("k***@example.com", logged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kaveri.nair@example.com", "k***@example.com")]
    [InlineData("a@b.in", "a***@b.in")]
    [InlineData("no-at-sign", "***")]
    public void MaskEmail_KeepsOnlyTheFirstCharacterAndTheDomain(string email, string expected)
    {
        Assert.Equal(expected, LogRedaction.MaskEmail(email));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }
}
