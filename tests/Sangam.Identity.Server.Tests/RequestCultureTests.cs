namespace Sangam.Identity.Server.Tests;

/// <summary>V-05: pages carry the request's culture; a browser asking for US English still gets en-IN.</summary>
[Collection("server")]
public sealed class RequestCultureTests
{
    private readonly SangamServerFactory _factory;

    public RequestCultureTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(null, "en-IN")]
    [InlineData("en-US,en;q=0.9", "en-IN")]
    [InlineData("hi-IN,hi;q=0.9", "hi-IN")]
    public async Task TheRegistrationPage_IsMarkedWithTheRequestCulture(string? acceptLanguage, string expected)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/register");
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"<html lang=\"{expected}\">", html, StringComparison.Ordinal);
    }
}
