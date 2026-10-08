using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// An e-mail or SMS template for one kind of message, in one language, at one level (SGM-209 §4). Where no level
/// has one, Sangam's built-in default is used.
/// </summary>
public sealed class MessageTemplate
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The level it applies at.</summary>
    public CustomisationScope Scope { get; set; }

    /// <summary>The application or organisation; null for the platform.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>The kind of message (<see cref="MessageTemplateKinds"/>).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Culture name, for example <c>hi-IN</c>.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>The e-mail subject; null for SMS.</summary>
    public string? Subject { get; set; }

    /// <summary>The text, with <c>{{variables}}</c> (e-mail) or <c>{#var#}</c> (SMS).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>For SMS: the template id registered on the DLT platform.</summary>
    public string? DltTemplateId { get; set; }

    /// <summary>Incremented on every change.</summary>
    public int Version { get; set; }

    /// <summary>Who changed it last.</summary>
    public Guid? UpdatedBy { get; set; }

    /// <summary>When it was changed last.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
