using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Server.Pages.Dev;

/// <summary>
/// Development-only view of captured e-mails and texts, so the code flows can be completed without a mail server.
/// With the shared outbox on (V-11) it shows what every host sent — the consoles' alerts too — labelled by host.
/// </summary>
public sealed class OutboxModel : PageModel
{
    private const int Count = 50;
    private readonly IWebHostEnvironment _environment;
    private readonly InMemoryEmailOutbox? _outbox;
    private readonly InMemorySmsOutbox? _smsOutbox;
    private readonly DevOutboxStore? _shared;

    /// <summary>Initialises the page.</summary>
    /// <param name="environment">Host environment.</param>
    /// <param name="outbox">The outbox, present only when <c>Sangam:Email:UseOutbox</c> is on.</param>
    /// <param name="smsOutbox">The SMS outbox, present only when the SMS provider is <c>outbox</c>.</param>
    /// <param name="shared">The outbox every host shares, when <c>Sangam:Email:SharedOutbox</c> is on.</param>
    public OutboxModel(IWebHostEnvironment environment, InMemoryEmailOutbox? outbox = null, InMemorySmsOutbox? smsOutbox = null, DevOutboxStore? shared = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _outbox = outbox;
        _smsOutbox = smsOutbox;
        _shared = shared;
    }

    /// <summary>Whether the list covers every host (V-11) or only this one.</summary>
    public bool Shared => _shared is not null;

    /// <summary>Captured text messages, newest first.</summary>
    public IReadOnlyList<OutboxEntry> TextMessages { get; private set; } = [];

    /// <summary>Captured e-mails, newest first.</summary>
    public IReadOnlyList<OutboxEntry> Messages { get; private set; } = [];

    /// <summary>Renders the outbox, or 404 outside Development.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment() || _outbox is null)
        {
            return NotFound();
        }

        if (_shared is not null)
        {
            Messages = [.. (await _shared.RecentAsync("email", Count, cancellationToken)).Select(m => new OutboxEntry(m.SentAt, m.Host, m.Recipient, m.Subject, m.Body))];
            TextMessages = [.. (await _shared.RecentAsync("sms", Count, cancellationToken)).Select(m => new OutboxEntry(m.SentAt, m.Host, m.Recipient, m.Subject, m.Body))];
        }
        else
        {
            Messages = [.. _outbox.Recent.Select(m => new OutboxEntry(m.SentAt, null, m.Message.ToEmail, m.Message.Subject, m.Message.TextBody))];
            TextMessages = [.. (_smsOutbox?.Recent ?? []).Select(t => new OutboxEntry(t.SentAt, null, t.Message.ToE164, t.Message.TemplateKey + " · " + t.Message.SenderHeader, t.Message.Text))];
        }

        return Page();
    }
}

/// <summary>One captured message.</summary>
/// <param name="SentAt">When it was captured.</param>
/// <param name="Host">The host that sent it, when the outbox is shared.</param>
/// <param name="Recipient">Address or number.</param>
/// <param name="Subject">Subject, or template and sender.</param>
/// <param name="Body">Text body.</param>
public sealed record OutboxEntry(DateTimeOffset SentAt, string? Host, string Recipient, string Subject, string Body);
