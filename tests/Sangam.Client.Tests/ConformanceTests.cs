using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sangam.Client.Audit;
using Sangam.Shared.Constants;

namespace Sangam.Client.Tests;

/// <summary>
/// R6: the shared conformance vectors (sdk/conformance/vectors.json), which every Sangam SDK runs: permissions over
/// sangam_orgs, step-up, the RFC 9470 challenge, webhook signatures and the shared audit schema.
/// </summary>
public sealed class ConformanceTests
{
    private static readonly JsonElement Vectors = Load();

    public static TheoryData<int> PermissionCases => Indexes("permissions", "has_permission");

    public static TheoryData<int> StepUpCases => Indexes("step_up", "cases");

    public static TheoryData<int> WebhookCases => Indexes("webhooks", "cases");

    public static TheoryData<int> AuditCases => Indexes("audit", "validation");

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public void Permissions(int i)
    {
        JsonElement c = Vectors.GetProperty("permissions").GetProperty("has_permission")[i];
        Assert.True(User().HasPermission(c.GetProperty("permission").GetString()!, c.GetProperty("org_path").GetString()!) == c.GetProperty("expect").GetBoolean(), c.GetProperty("why").GetString());
    }

    [Fact]
    public void Roles()
    {
        SangamUser user = User();
        foreach (JsonElement c in Vectors.GetProperty("permissions").GetProperty("has_role").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("expect").GetBoolean(), user.HasRole(c.GetProperty("role").GetString()!, c.GetProperty("org_path").GetString()!));
        }

        foreach (JsonElement c in Vectors.GetProperty("permissions").GetProperty("roles_in").EnumerateArray())
        {
            Assert.Equal([.. c.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!)], user.RolesIn(c.GetProperty("org_path").GetString()!).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void MalformedClaims_AreSkipped()
    {
        foreach (JsonElement c in Vectors.GetProperty("permissions").GetProperty("malformed_claims").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("memberships").GetInt32(), SangamClaimsPrincipalExtensions.ParseMemberships([new Claim(SangamClaims.Orgs, c.GetProperty("claim").GetString()!)]).Count);
        }
    }

    [Theory]
    [MemberData(nameof(StepUpCases))]
    public void StepUp(int i)
    {
        JsonElement s = Vectors.GetProperty("step_up");
        JsonElement c = s.GetProperty("cases")[i];
        List<Claim> claims = [new("sub", "x")];
        if (c.GetProperty("acr").ValueKind == JsonValueKind.String)
        {
            claims.Add(new Claim("acr", c.GetProperty("acr").GetString()!));
        }

        if (c.GetProperty("auth_time").ValueKind == JsonValueKind.Number)
        {
            claims.Add(new Claim("auth_time", c.GetProperty("auth_time").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        TimeSpan? maxAge = c.GetProperty("max_age").ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(c.GetProperty("max_age").GetInt32()) : null;
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(s.GetProperty("now").GetInt64());
        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "test"));
        Assert.True(principal.Satisfies(c.GetProperty("level").GetString()!, maxAge, now) == c.GetProperty("expect").GetBoolean(), c.GetProperty("why").GetString());
    }

    [Fact]
    public void Challenge()
    {
        foreach (JsonElement c in Vectors.GetProperty("challenge").EnumerateArray())
        {
            TimeSpan? maxAge = c.GetProperty("max_age").ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(c.GetProperty("max_age").GetInt32()) : null;
            Assert.Equal(c.GetProperty("header").GetString(), SangamStepUp.Challenge(c.GetProperty("level").GetString()!, maxAge));
        }
    }

    [Theory]
    [MemberData(nameof(WebhookCases))]
    public void Webhooks(int i)
    {
        JsonElement c = Vectors.GetProperty("webhooks").GetProperty("cases")[i];
        bool verified = SangamWebhook.Verify(c.GetProperty("id").GetString(), c.GetProperty("timestamp").GetString(), c.GetProperty("signature").GetString(), c.GetProperty("body").GetString()!,
            c.GetProperty("secret").GetString()!, DateTimeOffset.FromUnixTimeSeconds(c.GetProperty("now").GetInt64()));
        Assert.True(verified == c.GetProperty("expect").GetBoolean(), c.GetProperty("why").GetString());
    }

    [Theory]
    [MemberData(nameof(AuditCases))]
    public void AuditValidation(int i)
    {
        JsonElement c = Vectors.GetProperty("audit").GetProperty("validation")[i];
        IReadOnlyList<string> problems = SangamAudit.Validate(JsonNode.Parse(c.GetProperty("event").GetRawText()));
        Assert.True((problems.Count == 0) == c.GetProperty("valid").GetBoolean(), c.GetProperty("why").GetString() + ": " + string.Join("; ", problems));
    }

    [Fact]
    public void AuditBuilder()
    {
        JsonElement b = Vectors.GetProperty("audit").GetProperty("builder");
        JsonElement config = b.GetProperty("config");
        JsonElement u = b.GetProperty("user");
        JsonElement input = b.GetProperty("input");
        SangamAuditOptions options = new() { AppId = config.GetProperty("app_id").GetString()!, AppVersion = config.GetProperty("app_version").GetString()!, Environment = config.GetProperty("environment").GetString()! };
        List<Claim> claims = [new("sub", u.GetProperty("sub").GetString()!), new("sid", u.GetProperty("sid").GetString()!), new("acr", u.GetProperty("acr").GetString()!)];
        claims.AddRange(u.GetProperty("amr").EnumerateArray().Select(a => new Claim("amr", a.GetString()!)));
        ClaimsPrincipal user = new(new ClaimsIdentity(claims, "test"));
        JsonElement target = input.GetProperty("target");
        JsonElement tenant = input.GetProperty("tenant");
        SangamAuditEntry entry = new(input.GetProperty("action").GetString()!, input.GetProperty("category").GetString()!, target.GetProperty("type").GetString()!, target.GetProperty("id").GetString()!, input.GetProperty("outcome").GetString()!)
        {
            TargetDisplay = target.GetProperty("display").GetString(),
            OrganisationId = Guid.Parse(tenant.GetProperty("org_id").GetString()!),
            OrganisationPath = tenant.GetProperty("org_path").GetString(),
            DataClassification = input.GetProperty("data_classification").GetString()!,
            Changes = [.. input.GetProperty("changes").EnumerateArray().Select(ch => new SangamAuditChange(ch.GetProperty("field").GetString()!, ch.GetProperty("before").GetString(), ch.GetProperty("after").GetString()))],
            Sensitive = [.. input.GetProperty("sensitive").EnumerateArray().Select(s => s.GetString()!)],
        };
        JsonObject built = SangamAudit.Build(options, user, b.GetProperty("request").GetProperty("ip").GetString(), b.GetProperty("request").GetProperty("user_agent").GetString(), entry);
        Assert.Empty(SangamAudit.Validate(built));
        foreach (JsonProperty p in b.GetProperty("expect").EnumerateObject())
        {
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(p.Value.GetRawText()), built[p.Name]), p.Name + ": " + built[p.Name]?.ToJsonString());
        }

        foreach (JsonElement absent in b.GetProperty("absent").EnumerateArray())
        {
            Assert.False(built.ContainsKey(absent.GetString()!));
        }

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7", built["event_id"]!.GetValue<string>());
    }

    private static SangamUser User()
    {
        string orgs = Vectors.GetProperty("permissions").GetProperty("sangam_orgs").GetRawText();
        return new SangamUser(Guid.NewGuid(), "Test", null, SangamClaimsPrincipalExtensions.ParseMemberships([new Claim(SangamClaims.Orgs, orgs)]));
    }

    private static TheoryData<int> Indexes(string section, string list)
    {
        TheoryData<int> data = [];
        for (int i = 0; i < Vectors.GetProperty(section).GetProperty(list).GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static JsonElement Load()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sangam.sln")))
        {
            dir = dir.Parent;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "sdk", "conformance", "vectors.json")));
        return document.RootElement.Clone();
    }
}
