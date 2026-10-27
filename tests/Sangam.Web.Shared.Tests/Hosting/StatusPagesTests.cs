using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Sangam.Web.Shared.Hosting;

namespace Sangam.Web.Shared.Tests.Hosting;

/// <summary>rc.16: the 404 and error pages on all four hosts.</summary>
public sealed class StatusPagesTests
{
    [Fact]
    public void ABrowserAskingForAMissingPage_GetsThePage()
    {
        Assert.True(StatusPages.ShowsNotFoundPage(Request("/no-such-page")));
        Assert.True(StatusPages.ShowsNotFoundPage(Request("/no-such-page", method: "HEAD")));
    }

    [Fact]
    public void MachineEndpoints_AndAnswersWithABody_AreLeftAlone()
    {
        // An API or protocol endpoint's 404 stays as it is, and so does a 404 that already says something.
        Assert.False(StatusPages.ShowsNotFoundPage(Request("/api/v1/users/unknown")));
        Assert.False(StatusPages.ShowsNotFoundPage(Request("/connect/unknown")));
        Assert.False(StatusPages.ShowsNotFoundPage(Request("/_content/Sangam.Web.Shared/missing.css")));
        Assert.False(StatusPages.ShowsNotFoundPage(Request("/no-such-page", accept: "application/json")));
        Assert.False(StatusPages.ShowsNotFoundPage(Request("/no-such-page", method: "POST")));
        Assert.False(StatusPages.ShowsNotFoundPage(Request("/no-such-page", contentType: "application/problem+json")));
        Assert.False(StatusPages.ShowsNotFoundPage(Request("/somewhere", status: StatusCodes.Status200OK)));
    }

    [Fact]
    public void TheErrorPage_ShowsTheReference_AndNeverMore()
    {
        string html = StatusPages.Render(StatusCodes.Status500InternalServerError, "0HN7:0000002A", "help@sangamid.in");
        Assert.Contains("Something went wrong", html, StringComparison.Ordinal);
        Assert.Contains("<code>0HN7:0000002A</code>", html, StringComparison.Ordinal);
        Assert.Contains("mailto:help@sangamid.in", html, StringComparison.Ordinal);
        Assert.Contains("noindex", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNotFoundPage_HasNoReference_AndEncodesWhatItIsGiven()
    {
        string html = StatusPages.Render(StatusCodes.Status404NotFound, "ignored", "help+\"<x>\"@example.in");
        Assert.Contains("This page is not here", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<x>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePages_AreInTheVisitorsLanguage()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("hi-IN");
            string html = StatusPages.Render(StatusCodes.Status404NotFound, null, "help@sangamid.in");
            Assert.Contains("lang=\"hi-IN\"", html, StringComparison.Ordinal);
            Assert.Contains("यह पेज यहाँ नहीं है", html, StringComparison.Ordinal);
            Assert.Contains("<a href=\"mailto:help@sangamid.in\">help@sangamid.in</a>", html, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public async Task TheErrorPage_IsInTheRequestsLanguage_EvenOutsideItsCulture()
    {
        // The exception handler runs outside the localization middleware's culture; the page reads the request's choice.
        DefaultHttpContext context = Request("/somewhere", status: StatusCodes.Status200OK);
        context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(new RequestCulture("ml-IN"), null));
        using MemoryStream body = new();
        context.Response.Body = body;
        await StatusPages.WriteAsync(context, StatusCodes.Status500InternalServerError, "ref-1");
        string html = System.Text.Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains("lang=\"ml-IN\"", html, StringComparison.Ordinal);
        Assert.Contains("<code>ref-1</code>", html, StringComparison.Ordinal);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    private static DefaultHttpContext Request(string path, string method = "GET", string accept = "text/html,application/xhtml+xml", string? contentType = null, int status = StatusCodes.Status404NotFound)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Accept = accept;
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        return context;
    }
}
