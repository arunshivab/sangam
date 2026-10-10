using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Identity.Infrastructure.Customisation;

namespace Sangam.Identity.Infrastructure.Tests.Customisation;

/// <summary>rc.5 (ASVS V12.4.2): uploaded logos go through ClamAV's own protocol, and are never kept unscanned.</summary>
public sealed class ClamAvScannerTests
{
    // The EICAR test string: harmless, and recognised by every scanner as a test signature.
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    [Fact]
    public async Task ACleanFile_IsStreamedInChunks_AndPasses()
    {
        using FakeClamd clamd = new(content => "stream: OK");
        byte[] large = new byte[150_000];
        Random.Shared.NextBytes(large);

        ScanResult result = await Scanner(clamd.Port).ScanAsync(large);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal(large, clamd.Received);
        Assert.Equal("zINSTREAM", clamd.Command);
    }

    [Fact]
    public async Task AnInfectedFile_IsReported_WithItsSignature()
    {
        using FakeClamd clamd = new(content => content.AsSpan().SequenceEqual(Eicar) ? "stream: Eicar-Signature FOUND" : "stream: OK");

        ScanResult result = await Scanner(clamd.Port).ScanAsync(Eicar);

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("Eicar-Signature", result.Detail);
    }

    [Fact]
    public async Task AScannerThatCannotBeReached_IsUnavailable_SoTheUploadIsRefused()
    {
        int closedPort;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        ClamAvScanner scanner = Scanner(closedPort);
        Assert.Equal(ScanVerdict.Unavailable, (await scanner.ScanAsync(Eicar)).Verdict);
        Assert.False(await scanner.PingAsync());
    }

    [Fact]
    public async Task WithoutAHost_NothingIsScanned_AndProductionRefusesToStart()
    {
        ClamAvScanner scanner = new(new ConfigurationBuilder().Build(), NullLogger<ClamAvScanner>.Instance);
        Assert.False(scanner.Configured);
        Assert.Equal(ScanVerdict.NotConfigured, (await scanner.ScanAsync(Eicar)).Verdict);
        Assert.Null(await scanner.PingAsync());

        IConfiguration none = new ConfigurationBuilder().Build();
        Assert.Contains("virus scanner", AntivirusGuard.Validate("Production", none)!, StringComparison.Ordinal);
        Assert.Null(AntivirusGuard.Validate("Development", none));
        Assert.Null(AntivirusGuard.Validate("Production", Configuration(1)));
    }

    [Fact]
    public async Task APing_IsAnsweredWithPong()
    {
        using FakeClamd clamd = new(_ => "stream: OK");
        Assert.True(await Scanner(clamd.Port).PingAsync());
    }

    [Theory]
    [InlineData("stream: OK", ScanVerdict.Clean)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", ScanVerdict.Infected)]
    [InlineData("INSTREAM size limit exceeded. ERROR", ScanVerdict.Unavailable)]
    public void Replies_AreInterpreted(string reply, ScanVerdict expected) => Assert.Equal(expected, ClamAvScanner.Interpret(reply).Verdict);

    private static ClamAvScanner Scanner(int port) => new(Configuration(port), NullLogger<ClamAvScanner>.Instance);

    private static IConfiguration Configuration(int port) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [ClamAvScanner.HostKey] = "127.0.0.1",
        ["Sangam:Antivirus:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Sangam:Antivirus:TimeoutSeconds"] = "5",
    }).Build();

    /// <summary>Speaks just enough of clamd's protocol: INSTREAM chunks and PING.</summary>
    private sealed class FakeClamd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<byte[], string> _verdict;
        private readonly Task _loop;

        public FakeClamd(Func<byte[], string> verdict)
        {
            _verdict = verdict;
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public byte[] Received { get; private set; } = [];

        public string? Command { get; private set; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Dispose();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    NetworkStream stream = client.GetStream();
                    string command = await ReadCommandAsync(stream).ConfigureAwait(false);
                    Command = command;
                    if (command == "zPING")
                    {
                        await stream.WriteAsync("PONG\0"u8.ToArray()).ConfigureAwait(false);
                        continue;
                    }

                    using MemoryStream content = new();
                    byte[] length = new byte[4];
                    while (true)
                    {
                        await stream.ReadExactlyAsync(length).ConfigureAwait(false);
                        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(length);
                        if (size == 0)
                        {
                            break;
                        }

                        byte[] chunk = new byte[size];
                        await stream.ReadExactlyAsync(chunk).ConfigureAwait(false);
                        await content.WriteAsync(chunk).ConfigureAwait(false);
                    }

                    Received = content.ToArray();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_verdict(Received) + "\0")).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                // Stopped.
            }
        }

        private static async Task<string> ReadCommandAsync(NetworkStream stream)
        {
            StringBuilder command = new();
            byte[] one = new byte[1];
            while (await stream.ReadAsync(one).ConfigureAwait(false) == 1 && one[0] != 0)
            {
                command.Append((char)one[0]);
            }

            return command.ToString();
        }
    }
}
