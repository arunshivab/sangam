using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Pages.Dev;

/// <summary>Development-only view of captured emails, so the code flows can be completed without a mail server.</summary>
public sealed class OutboxModel : PageModel
{
    private readonly IWebHostEnvironment _environment;
    private readonly InMemoryEmailOutbox? _outbox;

    /// <summary>Initialises the page.</summary>
    /// <param name="environment">Host environment.</param>
    /// <param name="outbox">The outbox, present only when <c>Sangam:Email:UseOutbox</c> is on.</param>
    public OutboxModel(IWebHostEnvironment environment, InMemoryEmailOutbox? outbox = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _outbox = outbox;
    }

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
        return Page();
    }
}
