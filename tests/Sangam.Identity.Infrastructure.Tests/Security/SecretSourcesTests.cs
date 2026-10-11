using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Infrastructure.Security;

namespace Sangam.Identity.Infrastructure.Tests.Security;

/// <summary>
/// rc.8 (A6 workaround): secret settings come from the secret files or, by one setting, from a Vault-compatible KV version 2
/// store; a store that cannot be read stops the start rather than letting the host run without its secrets.
/// </summary>
public sealed class SecretSourcesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sangam-vault-").FullName;

    private string TokenFile => Path.Combine(_dir, "vault_token");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TheVault_SuppliesSettings_ReadWithTheTokenFromItsFile()
    {
        File.WriteAllText(TokenFile, "s.test-token\n");
        using Answering handler = new(HttpStatusCode.OK, """{"data":{"data":{"ConnectionStrings__Sangam":"Host=db;Password=from-vault","Sangam__PasswordHashing__Pepper":"pepper-from-vault"},"metadata":{"version":3}}}""");
        IConfiguration configuration = new ConfigurationBuilder().Add(Source(handler)).Build();

        Assert.Equal("Host=db;Password=from-vault", configuration.GetConnectionString("Sangam"));
        Assert.Equal("pepper-from-vault", configuration["Sangam:PasswordHashing:Pepper"]);
        Assert.Equal("https://vault.test:8200/v1/secret/data/sangam/identity", handler.Url);
        Assert.Equal("s.test-token", handler.Token);
    }

    [Fact]
    public void AVaultThatRefuses_OrCannotBeRead_StopsTheStart()
    {
        File.WriteAllText(TokenFile, "s.test-token");
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder().Add(Source(new Answering(HttpStatusCode.Forbidden, """{"errors":["permission denied"]}"""))).Build());
        Assert.Contains("403", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s.test-token", refused.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder().Add(Source(new Answering(HttpStatusCode.OK, """{"data":{"keys":["a"]}}"""))).Build());

        File.Delete(TokenFile);
        Assert.Throws<InvalidOperationException>(() => new ConfigurationBuilder().Add(Source(new Answering(HttpStatusCode.OK, "{}"))).Build());
    }

    [Fact]
    public void TheVaultsSettings_AreChecked()
    {
        Assert.Throws<InvalidOperationException>(() => VaultSecretsSource.From(Settings(("Address", "http://vault.test:8200"), ("Path", "sangam/identity"))));
        Assert.Throws<InvalidOperationException>(() => VaultSecretsSource.From(Settings(("Address", "https://vault.test:8200"))));
        VaultSecretsSource local = VaultSecretsSource.From(Settings(("Address", "http://127.0.0.1:8200"), ("Path", "/sangam/identity/")));
        Assert.Equal("sangam/identity", local.Path);
        Assert.Equal("secret", local.Mount);
        Assert.Equal("/run/secrets/vault_token", local.TokenFile);

        using ConfigurationManager bogus = new();
        bogus.AddInMemoryCollection(new Dictionary<string, string?> { [SecretSources.SourceKey] = "consul" });
        Assert.Throws<InvalidOperationException>(() => bogus.AddSangamSecrets());
    }

    private VaultSecretsSource Source(HttpMessageHandler handler) => new()
    {
        Address = new Uri("https://vault.test:8200/"),
        Path = "sangam/identity",
        TokenFile = TokenFile,
        Handler = () => handler,
    };

    private static IConfiguration Settings(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(SecretSources.VaultSection + ":" + v.Key, v.Value))).Build();

    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Url { get; private set; }

        public string? Token { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            Token = request.Headers.TryGetValues("X-Vault-Token", out IEnumerable<string>? values) ? values.Single() : null;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }
}
