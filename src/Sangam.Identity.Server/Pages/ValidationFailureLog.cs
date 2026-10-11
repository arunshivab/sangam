using Microsoft.AspNetCore.Mvc.Filters;

namespace Sangam.Identity.Server.Pages;

/// <summary>
/// rc.9 (ASVS V7.1.3): a form posted to a sign-in page that fails validation is logged as a security event — which page,
/// which handler and the names of the fields that failed, never their values (a mistyped password or code must not reach
/// the log). Repeated failures from one address show up beside the rate limits and lockouts.
/// </summary>
public sealed partial class ValidationFailureLog : IAsyncPageFilter
{
    private readonly ILogger<ValidationFailureLog> _logger;

    /// <summary>Initialises the filter.</summary>
    /// <param name="logger">Logger.</param>
    public ValidationFailureLog(ILogger<ValidationFailureLog> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        PageHandlerExecutedContext executed = await next().ConfigureAwait(false);
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method) || executed.ModelState.IsValid)
        {
            return;
        }

        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        string fields = string.Join(",", executed.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => Clip(e.Key)).Distinct().Take(10));
        string? remote = context.HttpContext.Connection.RemoteIpAddress?.ToString();
        LogInvalid(context.ActionDescriptor.ViewEnginePath, context.HandlerMethod?.Name, fields, remote);
    }

    private static string Clip(string key) => key.Length > 40 ? key[..40] : key;

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "Form on {Page} (handler {Handler}) failed validation for {Fields} from {RemoteIp}")]
    private partial void LogInvalid(string page, string? handler, string fields, string? remoteIp);
}
