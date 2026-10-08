using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Messaging;

namespace Sangam.Identity.Infrastructure.Tests.Messaging;

/// <summary>D-B and D-M: e-mail and SMS through Anjal's API — key, retries, allowlist, and nothing sensitive in the log.</summary>
public sealed class AnjalSendersTests
{
    private const string Code = "731964";
    private const string Key = "anjal-test-key-0123456789";

    private static EmailMessage Message(string to) => new(to, "Kaveri Nair", $"{Code} is your Sangam verification code", $"Your Sangam verification code is:\n\n    {Code}\n", "<p>" + Code + "</p>");

    [Fact]
    public async Task AnEmail_IsPostedWithTheKeyAndAnIdempotencyKey_AsTheContractSays()
    {
        using FakeAnjal anjal = new(HttpStatusCode.Accepted);
        (AnjalEmailSender sender, _) = EmailSender(anjal, new AnjalOptions());

        await sender.SendAsync(Message("kaveri@example.com"));

        FakeAnjal.Request request = Assert.Single(anjal.Requests);
        Assert.Equal("/api/v1/messages/email", request.Path);
        Assert.Equal("Bearer " + Key, request.Headers["Authorization"]);
        Assert.Equal(32, request.Headers[AnjalClient.IdempotencyHeader].Length);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal("no-reply@sangamid.in", body.RootElement.GetProperty("from").GetProperty("address").GetString());
        Assert.Equal("kaveri@example.com", body.RootElement.GetProperty("to").GetProperty("address").GetString());
        Assert.Contains(Code, body.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains(Code, body.RootElement.GetProperty("html").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABareKeyHeader_CanBeConfigured()
    {
        using FakeAnjal anjal = new(HttpStatusCode.OK);
        (AnjalEmailSender sender, _) = EmailSender(anjal, new AnjalOptions { ApiKeyHeader = "X-Api-Key", ApiKeyScheme = string.Empty });
        await sender.SendAsync(Message("kaveri@example.com"));
        Assert.Equal(Key, Assert.Single(anjal.Requests).Headers["X-Api-Key"]);
    }

    [Fact]
    public async Task Transient_Failures_AreRetried_WithTheSameIdempotencyKey()
    {
        using FakeAnjal anjal = new(HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.Accepted);
        (AnjalEmailSender sender, _) = EmailSender(anjal, new AnjalOptions());

        await sender.SendAsync(Message("kaveri@example.com"));

        Assert.Equal(3, anjal.Requests.Count);
        Assert.Single(anjal.Requests.Select(r => r.Headers[AnjalClient.IdempotencyHeader]).Distinct());
    }

    [Fact]
    public async Task A_BadRequest_IsNotRetried_AndTheSendFails()
    {
        using FakeAnjal anjal = new(HttpStatusCode.BadRequest, HttpStatusCode.Accepted);
        (AnjalEmailSender sender, _) = EmailSender(anjal, new AnjalOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Message("kaveri@example.com")));
        Assert.Single(anjal.Requests);
    }

    [Fact]
    public async Task AfterEveryAttempt_TheSendFails()
    {
        using FakeAnjal anjal = new(HttpStatusCode.BadGateway);
        (AnjalEmailSender sender, _) = EmailSender(anjal, new AnjalOptions { EmailAttempts = 3 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Message("kaveri@example.com")));
        Assert.Equal(3, anjal.Requests.Count);
    }

    [Fact]
    public async Task TheStagingAllowlist_RefusesOthers_BeforeAnythingLeaves()
    {
        using FakeAnjal anjal = new(HttpStatusCode.Accepted);
        AnjalOptions options = new() { AllowedRecipients = "@imagiqa.in, tester@example.com, +919876543210" };
        (AnjalEmailSender sender, _) = EmailSender(anjal, options);

        Assert.True(options.IsAllowed("arun@imagiqa.in"));
        Assert.True(options.IsAllowed("Tester@Example.com"));
        Assert.True(options.IsAllowed("+919876543210"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Message("stranger@example.com")));

        AnjalSmsSender sms = new(Client(anjal, options, new CapturingLogger<AnjalClient>()), options, new CapturingLogger<AnjalSmsSender>());
        SmsSendResult refused = await sms.SendAsync(new OutgoingSms("+919000000000", "sign_in", "1107000000000000001", "SANGAM", Code + " is your code"));
        Assert.False(refused.Accepted);
        Assert.Empty(anjal.Requests);
    }

    [Fact]
    public async Task TheLog_HoldsNeitherTheCodeNorTheSubjectNorTheFullAddress()
    {
        using FakeAnjal anjal = new(HttpStatusCode.InternalServerError, HttpStatusCode.Accepted);
        CapturingLogger<AnjalClient> log = new();
        AnjalOptions options = Configure(new AnjalOptions());
        AnjalEmailSender sender = new(Client(anjal, options, log), options, new CapturingLogger<AnjalEmailSender>());

        await sender.SendAsync(Message("kaveri.nair@example.com"));

        Assert.Equal(2, log.Lines.Count);
        foreach (string line in log.Lines)
        {
            Assert.DoesNotContain(Code, line, StringComparison.Ordinal);
            Assert.DoesNotContain("verification code", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kaveri.nair@", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Key, line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnSms_CarriesTheHeaderTemplateIdAndText_AndAnjalsId()
    {
        using FakeAnjal anjal = new(HttpStatusCode.Accepted);
        AnjalOptions options = Configure(new AnjalOptions());
        AnjalSmsSender sms = new(Client(anjal, options, new CapturingLogger<AnjalClient>()), options, new CapturingLogger<AnjalSmsSender>());

        SmsSendResult result = await sms.SendAsync(new OutgoingSms("+919876543210", "sign_in", "1107000000000000001", "SANGAM", Code + " is your SangamID sign-in code."));

        Assert.True(result.Accepted);
        Assert.Equal("anjal", result.Provider);
        Assert.Equal("msg-1", result.ProviderMessageId);
        FakeAnjal.Request request = Assert.Single(anjal.Requests);
        Assert.Equal("/api/v1/messages/sms", request.Path);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal("+919876543210", body.RootElement.GetProperty("to").GetString());
        Assert.Equal("SANGAM", body.RootElement.GetProperty("header").GetString());
        Assert.Equal("1107000000000000001", body.RootElement.GetProperty("dltTemplateId").GetString());
        Assert.Equal("sign_in", body.RootElement.GetProperty("templateKey").GetString());
    }

    [Fact]
    public async Task AnSms_IsTriedTwiceAtMost_BecauseThePersonIsWaiting()
    {
        using FakeAnjal anjal = new(HttpStatusCode.ServiceUnavailable);
        AnjalOptions options = Configure(new AnjalOptions());
        AnjalSmsSender sms = new(Client(anjal, options, new CapturingLogger<AnjalClient>()), options, new CapturingLogger<AnjalSmsSender>());
        SmsSendResult result = await sms.SendAsync(new OutgoingSms("+919876543210", "sign_in", "1", "SANGAM", "x"));
        Assert.False(result.Accepted);
        Assert.Equal("HTTP 503", result.Error);
        Assert.Equal(2, anjal.Requests.Count);
    }

    [Fact]
    public async Task InTheBackground_APageNeverWaits_AndTheMessageStillGoes()
    {
        using FakeAnjal anjal = new(HttpStatusCode.Accepted) { Hold = new TaskCompletionSource() };
        (AnjalEmailSender inner, _) = EmailSender(anjal, new AnjalOptions());
        using BackgroundEmailSender background = new(inner, new CapturingLogger<BackgroundEmailSender>());
        await background.StartAsync(CancellationToken.None);

        Task send = background.SendAsync(Message("kaveri@example.com"));
        Assert.True(send.IsCompletedSuccessfully);

        anjal.Hold.SetResult();
        for (int i = 0; i < 100 && anjal.Requests.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(anjal.Requests);
        await background.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void OnlyTimeoutsThrottlingAndServerErrors_AreRetried(HttpStatusCode status, bool transient)
        => Assert.Equal(transient, AnjalClient.IsTransient(status));

    private static AnjalOptions Configure(AnjalOptions options)
    {
        options.BaseUrl = "https://anjal.example.in/";
        options.ApiKey = Key;
        options.RetryDelay = TimeSpan.FromMilliseconds(1);
        return options;
    }

    private static (AnjalEmailSender Sender, CapturingLogger<AnjalClient> Log) EmailSender(FakeAnjal anjal, AnjalOptions options)
    {
        Configure(options);
        CapturingLogger<AnjalClient> log = new();
        return (new AnjalEmailSender(Client(anjal, options, log), options, new CapturingLogger<AnjalEmailSender>()), log);
    }

#pragma warning disable CA2000 // The client lives as long as the test; the handler is disposed by the test.
    private static AnjalClient Client(FakeAnjal anjal, AnjalOptions options, ILogger<AnjalClient> log)
        => new(new HttpClient(anjal) { BaseAddress = new Uri(options.BaseUrl ?? "https://anjal.example.in/") }, Configure(options), log)
        {
            Delay = (_, _) => Task.CompletedTask,
        };
#pragma warning restore CA2000

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

    /// <summary>Answers each request with the next status in turn (the last one repeats).</summary>
    private sealed class FakeAnjal : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _answers;

        public FakeAnjal(params HttpStatusCode[] answers)
        {
            _answers = answers;
        }

        public List<Request> Requests { get; } = [];

        public TaskCompletionSource? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hold is not null)
            {
                await Hold.Task.WaitAsync(cancellationToken);
            }

            Dictionary<string, string> headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            HttpStatusCode status;
            lock (Requests)
            {
                Requests.Add(new Request(request.RequestUri!.AbsolutePath, headers, body));
                status = _answers[Math.Min(Requests.Count - 1, _answers.Length - 1)];
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent((int)status < 300 ? "{\"id\":\"msg-1\"}" : "{\"error\":\"x\"}", Encoding.UTF8, "application/json"),
            };
        }

        public sealed record Request(string Path, Dictionary<string, string> Headers, string Body);
    }
}
