using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Server.Pages.Dev;

/// <summary>Development-only view of captured emails, so the code flows can be completed without a mail server.</summary>
public sealed class OutboxModel : PageModel
{
    private readonly IWebHostEnvironment _environment;
    private readonly InMemoryEmailOutbox? _outbox;
    private readonly InMemorySmsOutbox? _smsOutbox;

    /// <summary>Initialises the page.</summary>
    /// <param name="environment">Host environment.</param>
    /// <param name="outbox">The outbox, present only when <c>Sangam:Email:UseOutbox</c> is on.</param>
    /// <param name="smsOutbox">The SMS outbox, present only when the SMS provider is <c>outbox</c>.</param>
    public OutboxModel(IWebHostEnvironment environment, InMemoryEmailOutbox? outbox = null, InMemorySmsOutbox? smsOutbox = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _outbox = outbox;
        _smsOutbox = smsOutbox;
    }

    /// <summary>Captured text messages, newest first.</summary>
    public IReadOnlyList<SentSms> TextMessages { get; private set; } = [];

    /// <summary>Captured messages, newest first.</summary>
    public IReadOnlyList<SentEmail> Messages { get; private set; } = [];

    /// <summary>Renders the outbox, or 404 outside Development.</summary>
    public IActionResult OnGet()
    {
        if (!_environment.IsDevelopment() || _outbox is null)
        {
            return NotFound();
        }

        Messages = _outbox.Recent;
        TextMessages = _smsOutbox?.Recent ?? [];
        return Page();
    }
}
