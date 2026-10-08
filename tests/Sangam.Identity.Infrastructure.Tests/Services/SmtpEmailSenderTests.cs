using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Tests.Services;

/// <summary>PR-09: e-mail through SMTP, with an allowlist, and nothing sensitive in the log.</summary>
public sealed class SmtpEmailSenderTests
{
    private const string Code = "731964";

    private static EmailMessage Message(string to) => new(to, "Kaveri Nair", $"{Code} is your Sangam verification code", $"Your Sangam verification code is:\n\n    {Code}\n");

    [Fact]
    public async Task Send_DeliversTheMessageToTheSmtpServer()
    {
        using FakeSmtpServer server = new();
        SmtpEmailSender sender = Sender(server.Port, allowed: null, out _);

        await sender.SendAsync(Message("kaveri@example.com"));

        string received = Assert.Single(server.Messages);
        Assert.Contains("kaveri@example.com", received, StringComparison.Ordinal);
        Assert.Contains(Code, received, StringComparison.Ordinal);
        Assert.Contains("no-reply@sangamid.in", received, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowlist_RefusesOtherRecipients_AndSendsNothing()
    {
        using FakeSmtpServer server = new();
        SmtpEmailSender sender = Sender(server.Port, allowed: "@imagiqa.in, tester@example.com", out _);

        Assert.True(sender.IsAllowed("arun@imagiqa.in"));
        Assert.True(sender.IsAllowed("Tester@Example.com"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Message("stranger@example.com")));
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task TheLog_HoldsNeitherTheCodeNorTheSubjectNorTheFullAddress()
    {
        using FakeSmtpServer server = new();
        SmtpEmailSender sender = Sender(server.Port, allowed: null, out CapturingLogger logger);

        await sender.SendAsync(Message("kaveri.nair@example.com"));

        string line = Assert.Single(logger.Lines);
        Assert.DoesNotContain(Code, line, StringComparison.Ordinal);
        Assert.DoesNotContain("verification code", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kaveri.nair@", line, StringComparison.OrdinalIgnoreCase);
    }

    private static SmtpEmailSender Sender(int port, string? allowed, out CapturingLogger logger)
    {
        logger = new CapturingLogger();
        SmtpOptions options = new() { Host = "127.0.0.1", Port = port, Security = "None", AllowedRecipients = allowed, TimeoutSeconds = 10 };
        return new SmtpEmailSender(Options.Create(options), logger);
    }

    private sealed class CapturingLogger : ILogger<SmtpEmailSender>
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

    /// <summary>Just enough SMTP to receive messages in a test: EHLO, MAIL, RCPT, DATA, QUIT.</summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public List<string> Messages { get; } = [];

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                using (client)
                {
                    await ServeAsync(client);
                }
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII);
            using StreamWriter writer = new(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            StringBuilder envelope = new();
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync() is string line)
            {
                string verb = line.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                    case "HELO":
                        await writer.WriteLineAsync("250 fake");
                        break;
                    case "MAIL":
                    case "RCPT":
                        envelope.AppendLine(line);
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End with <CRLF>.<CRLF>");
                        StringBuilder data = new(envelope.ToString());
                        while (await reader.ReadLineAsync() is string d && d != ".")
                        {
                            data.AppendLine(d);
                        }

                        lock (Messages)
                        {
                            Messages.Add(data.ToString());
                        }

                        await writer.WriteLineAsync("250 Queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
    }
}
