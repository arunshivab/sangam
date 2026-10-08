using Microsoft.Extensions.Configuration;
using Sangam.Identity.Infrastructure;

namespace Sangam.Identity.Server.Tests;

/// <summary>PR-10: Docker secrets become settings; certificate files do not.</summary>
/// <remarks>In the server collection: it sets a process-wide environment variable that every host reads at start-up.</remarks>
[Collection("server")]
public sealed class SecretFilesTests
{
    [Fact]
    public void SecretFiles_BecomeSettings_AndCertificatesAreIgnored()
    {
        string dir = Directory.CreateTempSubdirectory("sangam-secrets").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "ConnectionStrings__Sangam"), "Host=db;Database=sangam_identity");
            File.WriteAllText(Path.Combine(dir, "Sangam__Portal__ClientSecret"), "s3cret");
            File.WriteAllBytes(Path.Combine(dir, "signing_current.pfx"), [0x30, 0x82, 0x01]);
            Environment.SetEnvironmentVariable(WebHosting.SecretsDirectoryVariable, dir);

            IConfiguration configuration = new ConfigurationBuilder().AddSangamSecretFiles().Build();

            Assert.Equal("Host=db;Database=sangam_identity", configuration.GetConnectionString("Sangam"));
            Assert.Equal("s3cret", configuration["Sangam:Portal:ClientSecret"]);
            Assert.Null(configuration["signing_current.pfx"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebHosting.SecretsDirectoryVariable, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AMissingSecretsDirectory_IsSkipped()
    {
        Environment.SetEnvironmentVariable(WebHosting.SecretsDirectoryVariable, Path.Combine(Path.GetTempPath(), "sangam-no-such-dir-" + Guid.NewGuid().ToString("N")));
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddSangamSecretFiles().Build();
            Assert.Empty(configuration.AsEnumerable());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebHosting.SecretsDirectoryVariable, null);
        }
    }
}
