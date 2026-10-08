using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Sangam.Identity.Infrastructure.Audit;

/// <summary>
/// The audit archive's tool (D-A), run through the identity server's own image or on the founder's own computer:
/// <list type="bullet">
/// <item><c>audit-archive keygen --out &lt;dir&gt; --password &lt;pass&gt;</c>: makes the archive key pair once. The
/// public certificate (<c>audit-archive.crt</c>) goes to the server; the encrypted private key
/// (<c>audit-archive.key.pem</c>) stays offline with the founder.</item>
/// <item><c>audit-archive list --dir &lt;dir&gt;</c>: the files and their clear-text headers (no key needed).</item>
/// <item><c>audit-archive read --file &lt;file&gt; --key &lt;key.pem&gt; --password &lt;pass&gt; [--out &lt;file.jsonl&gt;]</c>:
/// decrypts one file, checks it, and writes its events as JSON lines.</item>
/// </list>
/// </summary>
public static class AuditArchiveCommand
{
    /// <summary>The first argument that selects this tool.</summary>
    public const string Verb = "audit-archive";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Runs the tool; returns the process exit code.</summary>
    /// <param name="args">The arguments after <see cref="Verb"/>.</param>
    /// <param name="output">Where to write what happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i + 1 < args.Count; i += 2)
        {
            options[args[i].TrimStart('-')] = args[i + 1];
        }

        string command = args.Count > 0 ? args[0] : string.Empty;
        try
        {
            if (command == "keygen" && options.TryGetValue("out", out string? directory) && options.TryGetValue("password", out string? newPassword) && newPassword.Length >= 12)
            {
                (string certificate, string key) = KeyGen(directory, newPassword);
                await output.WriteLineAsync($"Public certificate (copy to the server): {certificate}").ConfigureAwait(false);
                await output.WriteLineAsync($"Private key (keep offline; without it the archive cannot be read): {key}").ConfigureAwait(false);
                return 0;
            }

            if (command == "list" && options.TryGetValue("dir", out string? dir))
            {
                foreach (string path in Directory.EnumerateFiles(dir, "*" + AuditArchiveFile.Extension).Order(StringComparer.Ordinal))
                {
                    AuditArchiveHeader? header = AuditArchiveFile.ReadHeader(path);
                    await output.WriteLineAsync(header is null
                        ? Path.GetFileName(path) + ": not an archive"
                        : string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)}: {header.Count} events, ids {header.FirstId}-{header.LastId}, {header.Oldest:yyyy-MM-dd} to {header.Newest:yyyy-MM-dd}, made {header.CreatedAt:yyyy-MM-dd}")).ConfigureAwait(false);
                }

                return 0;
            }

            if (command == "read" && options.TryGetValue("file", out string? file) && options.TryGetValue("key", out string? keyPath) && options.TryGetValue("password", out string? password))
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromEncryptedPem(await File.ReadAllTextAsync(keyPath, cancellationToken).ConfigureAwait(false), password);
                (AuditArchiveHeader header, IReadOnlyList<ArchivedAuditEvent> events) = AuditArchiveFile.Read(file, rsa);
                string? problem = Check(header, events);
                TextWriter target = output;
                StreamWriter? fileOut = options.TryGetValue("out", out string? outPath) ? new StreamWriter(outPath) : null;
                try
                {
                    if (fileOut is not null)
                    {
                        target = fileOut;
                    }

                    foreach (ArchivedAuditEvent e in events)
                    {
                        await target.WriteLineAsync(JsonSerializer.Serialize(e, Json)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (fileOut is not null)
                    {
                        await fileOut.DisposeAsync().ConfigureAwait(false);
                    }
                }

                await output.WriteLineAsync(problem ?? string.Create(CultureInfo.InvariantCulture, $"{events.Count} events read and checked.")).ConfigureAwait(false);
                return problem is null ? 0 : 1;
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            await output.WriteLineAsync("Failed: " + ex.Message).ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync("Usage: audit-archive keygen --out <directory> --password <12+ characters>").ConfigureAwait(false);
        await output.WriteLineAsync("       audit-archive list --dir <archive directory>").ConfigureAwait(false);
        await output.WriteLineAsync("       audit-archive read --file <archive file> --key <audit-archive.key.pem> --password <password> [--out <file.jsonl>]").ConfigureAwait(false);
        return 2;
    }

    /// <summary>Checks an archive's events against its header: count, ids, and the chain links between events.</summary>
    /// <param name="header">The header.</param>
    /// <param name="events">The events.</param>
    public static string? Check(AuditArchiveHeader header, IReadOnlyList<ArchivedAuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count != header.Count || events.Count == 0 || events[0].Id != header.FirstId || events[^1].Id != header.LastId)
        {
            return "The events do not match the header.";
        }

        if (!string.Equals(events[0].PrevHash, header.FirstPrevHash, StringComparison.Ordinal) || !string.Equals(events[^1].Hash, header.LastHash, StringComparison.Ordinal))
        {
            return "The chain hashes do not match the header.";
        }

        for (int i = 1; i < events.Count; i++)
        {
            if (events[i].PrevHash is not null && events[i - 1].Hash is not null && !string.Equals(events[i].PrevHash, events[i - 1].Hash, StringComparison.Ordinal))
            {
                return string.Create(CultureInfo.InvariantCulture, $"The chain is broken at event {events[i].Id}.");
            }
        }

        return null;
    }

    /// <summary>Makes the archive key pair: an RSA 3072 self-signed certificate and its password-protected private key.</summary>
    /// <param name="directory">Where to write them.</param>
    /// <param name="password">The private key's password.</param>
    public static (string Certificate, string Key) KeyGen(string directory, string password)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(password);
        Directory.CreateDirectory(directory);
        using RSA rsa = RSA.Create(3072);
        CertificateRequest request = new("CN=Sangam audit archive", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        string certificatePath = Path.Combine(directory, "audit-archive.crt");
        string keyPath = Path.Combine(directory, "audit-archive.key.pem");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem() + "\n");
        File.WriteAllText(keyPath, rsa.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000)) + "\n");
        return (certificatePath, keyPath);
    }
}
