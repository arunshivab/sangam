using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Audit;

/// <summary>
/// One encrypted audit-archive file (D-A). The server holds only the founder's public certificate: it can write an
/// archive but never read one back. Each file carries a random AES-256-GCM key wrapped with RSA-OAEP-SHA256 for that
/// certificate; the events inside are anonymised — IP addresses truncated (IPv4 to /24, IPv6 to /48) and user agents
/// dropped — and gzip-compressed JSON lines. A short clear-text header (dates, ids, count, the chain hashes at both
/// ends) is authenticated with the ciphertext, so the seven-year purge can decide without the private key and nobody
/// can alter the header unnoticed.
/// <code>
/// "SGMAUD01" | header length (int32 BE) | header JSON | wrapped-key length (int16 BE) | wrapped key | nonce (12) | tag (16) | ciphertext
/// </code>
/// </summary>
public static class AuditArchiveFile
{
    /// <summary>The file name extension.</summary>
    public const string Extension = ".sgmaud";

    private static readonly byte[] Magic = "SGMAUD01"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly string[] IpKeys = ["ip", "ip_address", "ipaddress", "client_ip", "remote_ip"];

    /// <summary>Writes the events, anonymised and encrypted for <paramref name="certificate"/>, and returns the header.</summary>
    /// <param name="path">The file to create (written to a temporary name, then renamed).</param>
    /// <param name="events">The events, oldest first.</param>
    /// <param name="certificate">The founder's archive certificate (public key only).</param>
    /// <param name="createdAt">When the archive is made.</param>
    public static AuditArchiveHeader Write(string path, IReadOnlyList<AuditEvent> events, X509Certificate2 certificate, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(certificate);
        if (events.Count == 0)
        {
            throw new ArgumentException("An archive needs at least one event.", nameof(events));
        }

        using RSA rsa = certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("The audit-archive certificate has no RSA public key.");
        AuditArchiveHeader header = new(
            1,
            createdAt,
            events.Count,
            events[0].Id,
            events[^1].Id,
            events.Min(e => e.OccurredAt),
            events.Max(e => e.OccurredAt),
            events[0].PrevHash,
            events[^1].Hash,
            certificate.Thumbprint);

        byte[] plain;
        using (MemoryStream buffer = new())
        {
            using (GZipStream gzip = new(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                foreach (AuditEvent e in events)
                {
                    gzip.Write(JsonSerializer.SerializeToUtf8Bytes(Anonymise(e), Json));
                    gzip.WriteByte((byte)'\n');
                }
            }

            plain = buffer.ToArray();
        }

        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[plain.Length];
        using (AesGcm aes = new(key, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag, headerBytes);
        }

        byte[] wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        CryptographicOperations.ZeroMemory(key);

        string temporary = path + ".partial";
        using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            Span<byte> number = stackalloc byte[4];
            file.Write(Magic);
            BinaryPrimitives.WriteInt32BigEndian(number, headerBytes.Length);
            file.Write(number);
            file.Write(headerBytes);
            BinaryPrimitives.WriteInt16BigEndian(number[..2], (short)wrapped.Length);
            file.Write(number[..2]);
            file.Write(wrapped);
            file.Write(nonce);
            file.Write(tag);
            file.Write(cipher);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path);
        return header;
    }

    /// <summary>Reads only the clear-text header (no key needed); <see langword="null"/> when the file is not an archive.</summary>
    /// <param name="path">The archive file.</param>
    public static AuditArchiveHeader? ReadHeader(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream file = File.OpenRead(path);
        return ReadHeader(file, out _);
    }

    /// <summary>Decrypts an archive with the founder's private key, checking the header and every byte.</summary>
    /// <param name="path">The archive file.</param>
    /// <param name="privateKey">The archive certificate's private key.</param>
    public static (AuditArchiveHeader Header, IReadOnlyList<ArchivedAuditEvent> Events) Read(string path, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] all = File.ReadAllBytes(path);
        using MemoryStream stream = new(all);
        AuditArchiveHeader header = ReadHeader(stream, out byte[] headerBytes) ?? throw new InvalidDataException("Not a Sangam audit archive.");
        Span<byte> number = stackalloc byte[2];
        stream.ReadExactly(number);
        byte[] wrapped = new byte[BinaryPrimitives.ReadInt16BigEndian(number)];
        stream.ReadExactly(wrapped);
        byte[] nonce = new byte[12];
        stream.ReadExactly(nonce);
        byte[] tag = new byte[16];
        stream.ReadExactly(tag);
        byte[] cipher = all[(int)stream.Position..];
        byte[] key = privateKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        byte[] plain = new byte[cipher.Length];
        try
        {
            using AesGcm aes = new(key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain, headerBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        List<ArchivedAuditEvent> events = [];
        using GZipStream gzip = new(new MemoryStream(plain), CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8);
        while (reader.ReadLine() is string line)
        {
            if (line.Length > 0)
            {
                events.Add(JsonSerializer.Deserialize<ArchivedAuditEvent>(line, Json) ?? throw new InvalidDataException("An archived event could not be read."));
            }
        }

        return (header, events);
    }

    /// <summary>The event as it is archived: IP truncated (also inside the metadata), user agent dropped.</summary>
    /// <param name="e">The live event.</param>
    public static ArchivedAuditEvent Anonymise(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new ArchivedAuditEvent(
            e.Id,
            e.OccurredAt,
            e.Action,
            e.ActorType.ToString(),
            e.ActorUserId,
            e.ActorAppId,
            e.TargetType,
            e.TargetId,
            AnonymiseMetadata(e.Metadata),
            TruncateIp(e.IpAddress),
            e.PrevHash,
            e.Hash);
    }

    /// <summary>An IPv4 address to its /24 (<c>203.0.113.0</c>), an IPv6 address to its /48; anything else to nothing.</summary>
    /// <param name="ip">The address, or <see langword="null"/>.</param>
    public static string? TruncateIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out IPAddress? address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();
        int keep = address.AddressFamily == AddressFamily.InterNetwork ? 3 : 6;
        for (int i = keep; i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }

        return new IPAddress(bytes).ToString();
    }

    private static string AnonymiseMetadata(string metadata)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(metadata);
        }
        catch (JsonException)
        {
            return "{}";
        }

        if (node is JsonObject obj)
        {
            foreach (string name in obj.Select(p => p.Key).ToList())
            {
                if (IpKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    obj[name] = TruncateIp(obj[name]?.GetValue<string>());
                }
                else if (name.Contains("user_agent", StringComparison.OrdinalIgnoreCase) || name.Equals("ua", StringComparison.OrdinalIgnoreCase))
                {
                    obj.Remove(name);
                }
            }
        }

        return node?.ToJsonString() ?? "{}";
    }

    private static AuditArchiveHeader? ReadHeader(Stream stream, out byte[] headerBytes)
    {
        headerBytes = [];
        Span<byte> magic = stackalloc byte[8];
        if (stream.Read(magic) != magic.Length || !magic.SequenceEqual(Magic))
        {
            return null;
        }

        Span<byte> number = stackalloc byte[4];
        stream.ReadExactly(number);
        int length = BinaryPrimitives.ReadInt32BigEndian(number);
        if (length is <= 0 or > 64 * 1024)
        {
            return null;
        }

        headerBytes = new byte[length];
        stream.ReadExactly(headerBytes);
        return JsonSerializer.Deserialize<AuditArchiveHeader>(headerBytes, Json);
    }
}

/// <summary>The clear-text, authenticated header of an audit archive (D-A).</summary>
/// <param name="Version">Format version.</param>
/// <param name="CreatedAt">When the archive was made.</param>
/// <param name="Count">How many events.</param>
/// <param name="FirstId">The first event's id.</param>
/// <param name="LastId">The last event's id.</param>
/// <param name="Oldest">The oldest event's time.</param>
/// <param name="Newest">The newest event's time (the seven-year purge goes by this).</param>
/// <param name="FirstPrevHash">The chain hash before the first event.</param>
/// <param name="LastHash">The last event's chain hash: the next archive, or the live table, continues from it.</param>
/// <param name="CertificateThumbprint">Which archive certificate the key is wrapped for.</param>
public sealed record AuditArchiveHeader(int Version, DateTimeOffset CreatedAt, int Count, long FirstId, long LastId, DateTimeOffset Oldest, DateTimeOffset Newest, string? FirstPrevHash, string? LastHash, string CertificateThumbprint);

/// <summary>One event as archived (D-A): IP truncated, no user agent; the original chain hashes kept.</summary>
/// <param name="Id">Event id.</param>
/// <param name="OccurredAt">When.</param>
/// <param name="Action">Action.</param>
/// <param name="ActorType">Actor type.</param>
/// <param name="ActorUserId">Acting user.</param>
/// <param name="ActorAppId">Acting application.</param>
/// <param name="TargetType">Target type.</param>
/// <param name="TargetId">Target.</param>
/// <param name="Metadata">Metadata JSON, with IP addresses truncated and user agents removed.</param>
/// <param name="IpAddress">The truncated IP address.</param>
/// <param name="PrevHash">The chain hash before this event (as it was computed over the original).</param>
/// <param name="Hash">This event's chain hash (over the original).</param>
public sealed record ArchivedAuditEvent(long Id, DateTimeOffset OccurredAt, string Action, string ActorType, Guid? ActorUserId, Guid? ActorAppId, string? TargetType, Guid? TargetId, string Metadata, string? IpAddress, string? PrevHash, string? Hash);
