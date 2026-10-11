using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// rc.8 (A6 workaround; vault brief section 10.1): where a host's secret settings come from, chosen in settings so the move
/// to imagiQa's vault is a configuration change. <c>Sangam:Secrets:Source</c> is <c>files</c> (the default: Docker secret
/// files, one setting per file) or <c>vault</c>: the settings are read at start-up from a key-value store that speaks the
/// OpenBao / HashiCorp Vault KV version 2 API (<c>GET {Address}/v1/{Mount}/data/{Path}</c>), which imagiQa's own vault is to
/// speak (brief, decision O2). The secret files are still read first, so a setting can move to the vault one at a time.
/// </summary>
/// <remarks>
/// <para>The vault's access token is itself a file (<c>Sangam:Secrets:Vault:TokenFile</c>, a Docker secret): the bootstrap
/// credential of decision O3. The address must be https, unless it is the same machine (loopback). With
/// <c>Sangam:Secrets:Vault:CaCertificatePath</c> only certificates issued by that authority are accepted.</para>
/// <para>If the vault cannot be read, the host refuses to start: it never falls back to running without its secrets.</para>
/// <para>Certificates (signing, encryption, key ring) stay files until the vault can sign and decrypt for Sangam (transit);
/// that move changes code, not only settings, and belongs to the vault project.</para>
/// </remarks>
public static class SecretSources
{
    /// <summary>The setting that chooses the source.</summary>
    public const string SourceKey = "Sangam:Secrets:Source";

    /// <summary>The vault's settings section.</summary>
    public const string VaultSection = "Sangam:Secrets:Vault";

    /// <summary>
    /// Adds the host's secrets: the secret files, then — when <see cref="SourceKey"/> is <c>vault</c> — the vault's.
    /// Call it straight after creating the builder, before anything reads a secret.
    /// </summary>
    /// <param name="configuration">The host's configuration (builder and view in one).</param>
    /// <returns>The same configuration.</returns>
    public static ConfigurationManager AddSangamSecrets(this ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.AddSangamSecretFiles();
        string source = configuration[SourceKey] ?? "files";
        if (string.Equals(source, "vault", StringComparison.OrdinalIgnoreCase))
        {
            ((IConfigurationBuilder)configuration).Add(VaultSecretsSource.From(configuration));
        }
        else if (!string.Equals(source, "files", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{SourceKey} is '{source}'; it must be 'files' or 'vault'. Refusing to start."); // i18n-ignore: a start-up log line for the operator
        }

        return configuration;
    }
}

/// <summary>Settings read from a Vault-compatible KV version 2 secret (see <see cref="SecretSources"/>).</summary>
public sealed class VaultSecretsSource : IConfigurationSource
{
    /// <summary>The vault's address, for example <c>https://vault.imagiqa.internal:8200</c>.</summary>
    public required Uri Address { get; init; }

    /// <summary>The KV version 2 mount (default <c>secret</c>).</summary>
    public string Mount { get; init; } = "secret";

    /// <summary>The secret's path within the mount, for example <c>sangam/identity</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The file holding the access token.</summary>
    public required string TokenFile { get; init; }

    /// <summary>The authority that issued the vault's certificate, when it is not publicly trusted.</summary>
    public string? CaCertificatePath { get; init; }

    /// <summary>How long the read may take.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Replaces the HTTP handler (tests).</summary>
    internal Func<HttpMessageHandler>? Handler { get; init; }

    /// <summary>Reads the source's settings from configuration.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <returns>The source.</returns>
    public static VaultSecretsSource From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SecretSources.VaultSection);
        if (!Uri.TryCreate(section["Address"], UriKind.Absolute, out Uri? address) || (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback)))
        {
            throw new InvalidOperationException($"{SecretSources.VaultSection}:Address must be the vault's https address (http only on this machine). Refusing to start."); // i18n-ignore: a start-up log line for the operator
        }

        string path = section["Path"] is { Length: > 0 } p ? p.Trim('/') : throw new InvalidOperationException($"{SecretSources.VaultSection}:Path is missing. Refusing to start."); // i18n-ignore: a start-up log line for the operator
        return new VaultSecretsSource
        {
            Address = address,
            Mount = section["Mount"] is { Length: > 0 } mount ? mount.Trim('/') : "secret",
            Path = path,
            TokenFile = section["TokenFile"] is { Length: > 0 } token ? token : "/run/secrets/vault_token",
            CaCertificatePath = section["CaCertificatePath"] is { Length: > 0 } ca ? ca : null,
        };
    }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new VaultSecretsProvider(this);

    /// <summary>Creates the HTTP handler: certificate revocation checked, and the configured authority only, if any.</summary>
    internal HttpMessageHandler CreateHandler()
    {
        if (Handler is not null)
        {
            return Handler();
        }

        SocketsHttpHandler handler = TlsRevocation.CreateHandler();
        handler.AllowAutoRedirect = false;
        if (CaCertificatePath is not null)
        {
            X509Certificate2 authority = X509CertificateLoader.LoadCertificateFromFile(CaCertificatePath);
            X509ChainPolicy policy = TlsRevocation.Policy();
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.Add(authority);
            handler.SslOptions.CertificateChainPolicy = policy;
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, chain, errors) =>
                errors == SslPolicyErrors.None || TlsRevocation.Acceptable(errors, chain);
        }

        return handler;
    }
}

/// <summary>Loads <see cref="VaultSecretsSource"/> once, at start-up.</summary>
internal sealed class VaultSecretsProvider : ConfigurationProvider
{
    private readonly VaultSecretsSource _source;

    public VaultSecretsProvider(VaultSecretsSource source)
    {
        _source = source;
    }

    public override void Load()
    {
        string token;
        try
        {
            token = File.ReadAllText(_source.TokenFile).Trim();
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"The vault token file {_source.TokenFile} cannot be read ({ex.GetType().Name}). Refusing to start.", ex); // i18n-ignore: a start-up log line for the operator
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"The vault token file {_source.TokenFile} cannot be read ({ex.GetType().Name}). Refusing to start.", ex); // i18n-ignore: a start-up log line for the operator
        }

        Uri url = new(_source.Address, $"v1/{_source.Mount}/data/{_source.Path}");
        using HttpClient client = new(_source.CreateHandler(), disposeHandler: true) { Timeout = _source.Timeout };
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Add("X-Vault-Token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        string body;
        try
        {
            using HttpResponseMessage response = client.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                // The vault's own error text is left out: it is not needed to act, and never contains the secret.
                throw new InvalidOperationException($"The vault at {_source.Address} answered {(int)response.StatusCode} for {_source.Mount}/{_source.Path}. Refusing to start."); // i18n-ignore: a start-up log line for the operator
            }

            using StreamReader reader = new(response.Content.ReadAsStream());
            body = reader.ReadToEnd();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"The vault at {_source.Address} cannot be reached ({ex.Message}). Refusing to start.", ex); // i18n-ignore: a start-up log line for the operator
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"The vault at {_source.Address} did not answer within {_source.Timeout.TotalSeconds:0} seconds. Refusing to start.", ex); // i18n-ignore: a start-up log line for the operator
        }

        Data = Parse(body);
    }

    /// <summary>Reads a KV version 2 answer: <c>{"data":{"data":{"Name__Key":"value"}}}</c>, <c>__</c> standing for <c>:</c>.</summary>
    internal static Dictionary<string, string?> Parse(string body)
    {
        Dictionary<string, string?> data = new(StringComparer.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out JsonElement outer) || !outer.TryGetProperty("data", out JsonElement inner) || inner.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The vault's answer is not a KV version 2 secret. Refusing to start."); // i18n-ignore: a start-up log line for the operator
        }

        foreach (JsonProperty property in inner.EnumerateObject())
        {
            data[property.Name.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal)] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
        }

        return data;
    }
}
