using System.Net.Http.Headers;
using System.Text.Json;
using Imagiqa.Web.Records;

namespace Imagiqa.Web.Demo;

/// <summary>
/// V-15: at demo.sangamid.in an invited tester would otherwise land on "You have no role at a hospital", and every one
/// of them would need a role given by hand. In demo mode (<c>Imagiqa:Demo</c>) the tester can join a made-up hospital,
/// <em>Demo Hospital</em>, as a doctor or a nurse with one click. imagiQa does it the way any application would:
/// through Sangam's management API with its own client credentials (<c>sangam.manage</c>), creating the hospital and
/// the two roles if they are not there yet. The tester signs in again so the new role is in their token.
/// </summary>
public sealed class DemoHospital
{
    /// <summary>The demo hospital's fixed id, so every tester joins the same one.</summary>
    public static readonly Guid DefaultId = new("d3770000-0000-4000-8000-000000000001");

    /// <summary>Its name: made up, and says so.</summary>
    public const string Name = "Demo Hospital (made up)";

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the service.</summary>
    /// <param name="http">A client for Sangam.</param>
    /// <param name="configuration">Reads Sangam:Authority, :ClientId, :ClientSecret and Imagiqa:Demo.</param>
    public DemoHospital(HttpClient http, IConfiguration configuration)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>Whether this is the demo.</summary>
    public bool Enabled => _configuration.GetValue("Imagiqa:Demo", false);

    /// <summary>The demo hospital's id (<c>Imagiqa:DemoHospitalId</c>, or the fixed default).</summary>
    public Guid HospitalId => Guid.TryParse(_configuration["Imagiqa:DemoHospitalId"], out Guid id) ? id : DefaultId;

    /// <summary>Gives the person the role at the demo hospital; <see langword="null"/> when done, else why not.</summary>
    /// <param name="userId">The person (their Sangam subject).</param>
    /// <param name="role"><c>doctor</c> or <c>nurse</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string?> JoinAsync(Guid userId, string role, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return "Joining the demo hospital is only possible on the demo.";
        }

        if (role is not (ImagiqaRoles.Doctor or ImagiqaRoles.Nurse))
        {
            return "Choose doctor or nurse.";
        }

        string authority = (_configuration["Sangam:Authority"] ?? string.Empty).TrimEnd('/');
        string? token = await TokenAsync(authority, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return "Sangam did not let imagiQa manage its demo hospital (client credentials with sangam.manage).";
        }

        (string Path, object Body)[] calls =
        [
            ("/api/v1/roles/" + ImagiqaRoles.Doctor, new { displayName = "Doctor", description = "Registers and finds patients, reads vitals, writes consultation notes.", permissions = new[] { "patients:read", "patients:write", "vitals:read", "notes:write" }, orgId = (Guid?)null }),
            ("/api/v1/roles/" + ImagiqaRoles.Nurse, new { displayName = "Nurse", description = "Registers and finds patients, records vitals, reads consultation notes.", permissions = new[] { "patients:read", "patients:write", "vitals:write", "notes:read" }, orgId = (Guid?)null }),
            ($"/api/v1/orgs/{HospitalId:D}", new { name = Name, type = "hospital", parentId = (Guid?)null, metadata = "{\"demo\":true}" }),
            ($"/api/v1/orgs/{HospitalId:D}/members/{userId:D}", new { role, appliesToDescendants = false }),
        ];
        foreach ((string path, object body) in calls)
        {
            using HttpRequestMessage request = new(HttpMethod.Put, new Uri(authority + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return $"Sangam refused {path} ({(int)response.StatusCode}).";
            }
        }

        return null;
    }

    private async Task<string?> TokenAsync(string authority, CancellationToken cancellationToken)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _configuration["Sangam:ClientId"] ?? string.Empty,
            ["client_secret"] = _configuration["Sangam:ClientSecret"] ?? string.Empty,
            ["scope"] = "sangam.manage",
        });
        using HttpResponseMessage response = await _http.PostAsync(new Uri(authority + "/connect/token"), form, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return json.RootElement.TryGetProperty("access_token", out JsonElement value) ? value.GetString() : null;
    }
}
