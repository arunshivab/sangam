using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Sangam.Identity.Infrastructure.Customisation;

/// <summary>The outcome of a virus scan (rc.5).</summary>
public enum ScanVerdict
{
    /// <summary>Scanned and clean.</summary>
    Clean = 0,

    /// <summary>Scanned and infected; the file must be refused.</summary>
    Infected = 1,

    /// <summary>Not scanned: the scanner could not be reached or did not answer. The file must be refused.</summary>
    Unavailable = 2,

    /// <summary>Not scanned because no scanner is configured — allowed only in Development and Testing.</summary>
    NotConfigured = 3,
}

/// <summary>A scan's verdict and, for an infected file, what was found.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Detail">The signature found, or why the scan could not run.</param>
public sealed record ScanResult(ScanVerdict Verdict, string? Detail = null);

/// <summary>Scans uploaded files for known malware before they are kept (rc.5, ASVS V12.4.2).</summary>
public interface IFileScanner
{
    /// <summary>Whether a scanner is configured.</summary>
    bool Configured { get; }

    /// <summary>Scans <paramref name="content"/>.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ScanResult> ScanAsync(byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Whether the scanner answers (for the monitoring page): <see langword="null"/> when none is configured.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool?> PingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// ClamAV over its own network protocol (<c>clamd</c>, <c>INSTREAM</c>), with no package added: the server's ClamAV,
/// shared with Anjal's mail scanning, on the server's private network only (rc.5, ASVS V12.4.2). Settings under
/// <c>Sangam:Antivirus</c>: <c>Host</c>, <c>Port</c> (default 3310) and <c>TimeoutSeconds</c> (default 10). A file is
/// never kept unscanned: when the scanner cannot be reached the caller refuses the upload.
/// </summary>
public sealed partial class ClamAvScanner : IFileScanner
{
    /// <summary>Configuration key of the scanner's host name.</summary>
    public const string HostKey = "Sangam:Antivirus:Host";

    private const int ChunkBytes = 64 * 1024;
    private readonly string? _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly ILogger<ClamAvScanner> _logger;

    /// <summary>Initialises the scanner.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <param name="logger">Logger.</param>
    public ClamAvScanner(IConfiguration configuration, ILogger<ClamAvScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        string? host = configuration[HostKey];
        _host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        _port = Math.Clamp(configuration.GetValue("Sangam:Antivirus:Port", 3310), 1, 65535);
        _timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Sangam:Antivirus:TimeoutSeconds", 10), 1, 60));
    }

    /// <inheritdoc />
    public bool Configured => _host is not null;

    /// <inheritdoc />
    public async Task<ScanResult> ScanAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_host is null)
        {
            return new ScanResult(ScanVerdict.NotConfigured);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using TcpClient tcp = new();
            await tcp.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = tcp.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                byte[] length = new byte[4];
                for (int offset = 0; offset < content.Length; offset += ChunkBytes)
                {
                    int size = Math.Min(ChunkBytes, content.Length - offset);
                    BinaryPrimitives.WriteUInt32BigEndian(length, (uint)size);
                    await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                    await stream.WriteAsync(content.AsMemory(offset, size), timeout.Token).ConfigureAwait(false);
                }

                BinaryPrimitives.WriteUInt32BigEndian(length, 0);
                await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                string reply = await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false);
                return Interpret(reply);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogUnavailable(_host, _port, ex.GetType().Name);
            return new ScanResult(ScanVerdict.Unavailable, ex is OperationCanceledException ? "The virus scanner did not answer in time." : "The virus scanner could not be reached.");
        }
    }

    /// <inheritdoc />
    public async Task<bool?> PingAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using TcpClient tcp = new();
            await tcp.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = tcp.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync("zPING\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                return string.Equals(await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false), "PONG", StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    /// <summary>Reads clamd's verdict: <c>stream: OK</c>, <c>stream: NAME FOUND</c> or an error.</summary>
    /// <param name="reply">clamd's reply, without its terminating zero.</param>
    public static ScanResult Interpret(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        string text = reply.Trim();
        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            int colon = text.IndexOf(':', StringComparison.Ordinal);
            string name = text[(colon + 1)..^" FOUND".Length].Trim();
            return new ScanResult(ScanVerdict.Infected, name);
        }

        return text.EndsWith(": OK", StringComparison.Ordinal) || string.Equals(text, "OK", StringComparison.Ordinal)
            ? new ScanResult(ScanVerdict.Clean)
            : new ScanResult(ScanVerdict.Unavailable, string.Create(CultureInfo.InvariantCulture, $"The virus scanner answered: {(text.Length > 120 ? text[..120] : text)}"));
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[512];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (buffer[total - 1] == 0)
            {
                total--;
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "The virus scanner at {Host}:{Port} is unavailable ({Error}); the upload was refused")]
    private partial void LogUnavailable(string host, int port, string error);
}
