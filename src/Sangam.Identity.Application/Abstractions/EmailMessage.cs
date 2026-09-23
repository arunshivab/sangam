namespace Sangam.Identity.Application.Abstractions;

/// <summary>A transactional email to one recipient.</summary>
/// <param name="ToEmail">Recipient address.</param>
/// <param name="ToName">Recipient display name.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="TextBody">Plain-text body (always present; every mail must read without HTML).</param>
/// <param name="HtmlBody">Optional HTML body.</param>
public sealed record EmailMessage(string ToEmail, string ToName, string Subject, string TextBody, string? HtmlBody = null);
