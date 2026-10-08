using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// Bot friction without a third-party CAPTCHA (D-050, D-111, OI-034): a honeypot field that people
/// never see, and a signed form timestamp that refuses posts made faster than a person could.
/// Works without JavaScript (D-063).
/// </summary>
public static class Antibot
{
    /// <summary>The honeypot field: hidden from people, often filled by bots.</summary>
    public const string HoneypotField = "website";

    /// <summary>The signed timestamp field.</summary>
    public const string TimestampField = "__form_ts";

    /// <summary>ViewData key carrying the refusal message to the form.</summary>
    public const string RefusalKey = "SangamFormRefused";

    /// <summary>Configuration key: minimum seconds between showing a form and accepting its post (default 2).</summary>
    public const string MinimumSecondsKey = "Sangam:Antibot:MinimumSeconds";

    /// <summary>The message shown on refusal: the same for every reason, revealing nothing.</summary>
    public const string Refusal = "We could not accept that form. Please wait a moment and try again.";

    private const string Purpose = "Sangam.Antibot.FormTimestamp";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    /// <summary>Creates the signed timestamp for a form being shown.</summary>
    /// <param name="provider">Data-protection provider.</param>
    /// <param name="now">The current time.</param>
    public static string CreateToken(IDataProtectionProvider provider, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.CreateProtector(Purpose).ToTimeLimitedDataProtector()
            .Protect(now.UtcTicks.ToString(CultureInfo.InvariantCulture), TokenLifetime);
    }

    /// <summary>Returns why a post must be refused, or <see langword="null"/> when it is acceptable.</summary>
    /// <param name="provider">Data-protection provider.</param>
    /// <param name="honeypot">Value of the honeypot field.</param>
    /// <param name="token">Value of the timestamp field.</param>
    /// <param name="now">The current time.</param>
    /// <param name="minimum">Minimum time since the form was shown.</param>
    public static string? Check(IDataProtectionProvider provider, string? honeypot, string? token, DateTimeOffset now, TimeSpan minimum)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!string.IsNullOrEmpty(honeypot))
        {
            return "honeypot";
        }

        if (string.IsNullOrEmpty(token))
        {
            return "missing";
        }

        try
        {
            string ticks = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(token);
            DateTimeOffset shown = new(long.Parse(ticks, CultureInfo.InvariantCulture), TimeSpan.Zero);
            return now - shown < minimum ? "too-fast" : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "invalid";
        }
        catch (FormatException)
        {
            return "invalid";
        }
    }
}

/// <summary>Applies <see cref="Antibot"/> to a page’s POST handlers.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed partial class AntibotAttribute : Attribute, IAsyncPageFilter
{
    /// <inheritdoc />
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (HttpMethods.IsPost(context.HttpContext.Request.Method) && context.HandlerInstance is PageModel page)
        {
            IServiceProvider services = context.HttpContext.RequestServices;
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            TimeSpan minimum = TimeSpan.FromSeconds(configuration.GetValue(Antibot.MinimumSecondsKey, 2));
            IFormCollection form = context.HttpContext.Request.Form;
            string? reason = Antibot.Check(services.GetRequiredService<IDataProtectionProvider>(), form[Antibot.HoneypotField], form[Antibot.TimestampField], services.GetRequiredService<IClock>().UtcNow, minimum);
            if (reason is not null)
            {
                ILogger<AntibotAttribute> logger = services.GetRequiredService<ILogger<AntibotAttribute>>();
                string pagePath = context.ActionDescriptor.ViewEnginePath;
                LogRefused(logger, pagePath, reason);
                page.ViewData[Antibot.RefusalKey] = Antibot.Refusal;
                context.Result = page.Page();
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1300, Level = LogLevel.Information, Message = "Bot check refused a form post to {Page}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string page, string reason);
}
