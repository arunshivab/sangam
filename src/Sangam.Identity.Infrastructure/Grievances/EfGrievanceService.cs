using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Grievances;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Shared;

namespace Sangam.Identity.Infrastructure.Grievances;

/// <summary>The grievance log (D-D): see <see cref="IGrievanceService"/>.</summary>
public sealed class EfGrievanceService : IGrievanceService
{
    private readonly SangamDbContext _db;
    private readonly GrievanceClock _deadlines;
    private readonly IEmailSender _email;
    private readonly IMessageTemplates _templates;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="deadlines">Deadlines.</param>
    /// <param name="email">E-mail.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfGrievanceService(SangamDbContext db, GrievanceClock deadlines, IEmailSender email, IMessageTemplates templates, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _deadlines = deadlines ?? throw new ArgumentNullException(nameof(deadlines));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GrievanceRow>?> ListAsync(Guid operatorUserId, bool openOnly, CancellationToken cancellationToken = default)
    {
        if (await RoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        IQueryable<Grievance> query = _db.Grievances.AsNoTracking();
        if (openOnly)
        {
            query = query.Where(g => g.ClosedAt == null);
        }

        List<Grievance> rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows
            .OrderBy(g => g.ClosedAt is null ? 0 : 1)
            .ThenBy(g => g.ClosedAt is null ? (g.AcknowledgedAt is null ? g.AcknowledgeBy : g.ResolveBy) : DateTimeOffset.MaxValue)
            .ThenByDescending(g => g.ReceivedAt)
            .Select(g => Row(g, now))];
    }

    /// <inheritdoc />
    public async Task<GrievanceDetail?> GetAsync(Guid operatorUserId, Guid id, CancellationToken cancellationToken = default)
    {
        PlatformRole? role = await RoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return null;
        }

        Grievance? g = await _db.Grievances.AsNoTracking().Include(x => x.Entries).SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        if (g is null)
        {
            return null;
        }

        List<Guid> operators = [.. g.Entries.Select(e => e.OperatorUserId).Distinct()];
        Dictionary<Guid, string> names = await _db.Users.AsNoTracking()
            .Where(u => operators.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, cancellationToken).ConfigureAwait(false);
        string? accountEmail = g.UserId is Guid userId
            ? await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return new GrievanceDetail(
            Row(g, _clock.UtcNow),
            g.Channel,
            g.ComplainantContact,
            accountEmail,
            g.Summary,
            g.Resolution,
            [.. g.Entries.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new GrievanceStep(e.At, names.GetValueOrDefault(e.OperatorUserId, "—"), e.Kind, e.Text))],
            role >= PlatformRole.Support);
    }

    /// <inheritdoc />
    public async Task<(AdminResult Result, Guid? Id)> LogAsync(Guid operatorUserId, GrievanceInput input, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await RequireActAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is AdminResult refusal)
        {
            return (refusal, null);
        }

        DateTimeOffset now = _clock.UtcNow;
        string name = input.Name.Trim();
        string contact = input.Contact.Trim();
        string summary = input.Summary.Trim();
        if (name.Length == 0 || contact.Length == 0 || summary.Length < 10)
        {
            return (AdminResult.Refused("Give the person's name, how to reach them, and what the grievance is about."), null);
        }

        if (!GrievanceLists.Channels.Contains(input.Channel) || !GrievanceLists.Categories.Contains(input.Category))
        {
            return (AdminResult.Refused("Choose how it arrived and what it is about."), null);
        }

        if (input.ReceivedAt > now.AddMinutes(5) || input.ReceivedAt < now.AddDays(-60))
        {
            return (AdminResult.Refused("The date received must be within the last 60 days."), null);
        }

        string normalized = contact.ToUpperInvariant();
        Guid? userId = contact.Contains('@', StringComparison.Ordinal)
            ? await _db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await _db.Users.AsNoTracking().Where(u => u.PhoneNumber == contact).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        Grievance grievance = new()
        {
            Id = Guid.NewGuid(),
            ReceivedAt = input.ReceivedAt,
            Channel = input.Channel,
            Category = input.Category,
            ComplainantName = name.Length > 200 ? name[..200] : name,
            ComplainantContact = contact.Length > 320 ? contact[..320] : contact,
            UserId = userId,
            Summary = summary.Length > 4000 ? summary[..4000] : summary,
            Status = GrievanceStatus.Received,
            AcknowledgeBy = _deadlines.AcknowledgeBy(input.ReceivedAt),
            ResolveBy = _deadlines.ResolveBy(input.ReceivedAt),
            CreatedByUserId = operatorUserId,
        };
        grievance.Entries.Add(new GrievanceEntry { At = now, OperatorUserId = operatorUserId, Kind = "logged", Text = summary.Length > 4000 ? summary[..4000] : summary });

        // The reference is the year's next number; two operators logging at once retry with the next one.
        for (int attempt = 0; ; attempt++)
        {
            int year = IndiaTime.ToIndia(input.ReceivedAt).Year;
            string prefix = string.Create(CultureInfo.InvariantCulture, $"GRV-{year}-");
            int count = await _db.Grievances.CountAsync(g => g.Reference.StartsWith(prefix), cancellationToken).ConfigureAwait(false);
            grievance.Reference = prefix + (count + 1 + attempt).ToString("D4", CultureInfo.InvariantCulture);
            _db.Grievances.Add(grievance);
            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                _db.ChangeTracker.Clear();
                grievance.Entries.ForEach(e => e.Id = 0);
            }
        }

        await AuditAsync(AuditActions.GrievanceLog, operatorUserId, grievance, ipAddress, cancellationToken, ("category", grievance.Category), ("channel", grievance.Channel)).ConfigureAwait(false);
        return (AdminResult.Ok($"Grievance {grievance.Reference} logged. Acknowledge it by {IndiaTime.Stamp(grievance.AcknowledgeBy)}."), grievance.Id);
    }

    /// <inheritdoc />
    public async Task<AdminResult> AcknowledgeAsync(Guid operatorUserId, Guid id, bool sendEmail, string? ipAddress, CancellationToken cancellationToken = default)
    {
        (AdminResult? refusal, Grievance? g) = await OpenForActionAsync(operatorUserId, id, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (g!.AcknowledgedAt is not null)
        {
            return AdminResult.Refused("It has already been acknowledged.");
        }

        DateTimeOffset now = _clock.UtcNow;
        bool emailed = sendEmail && await SendAsync(MessageTemplateKinds.GrievanceAcknowledgement, g, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reference"] = g.Reference,
            ["received"] = IndiaTime.Date(g.ReceivedAt),
            ["resolve_by"] = IndiaTime.Date(g.ResolveBy),
        }, cancellationToken).ConfigureAwait(false);
        g.AcknowledgedAt = now;
        g.Status = GrievanceStatus.Acknowledged;
        g.Entries.Add(new GrievanceEntry { At = now, OperatorUserId = operatorUserId, Kind = "acknowledged", Text = emailed ? "Acknowledgement e-mailed." : "The grievance was acknowledged." });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.GrievanceAcknowledge, operatorUserId, g, ipAddress, cancellationToken, ("emailed", emailed ? "true" : "false"), ("late", now > g.AcknowledgeBy ? "true" : "false")).ConfigureAwait(false);
        return AdminResult.Ok(emailed ? "Acknowledged, and the acknowledgement was e-mailed." : "The grievance was acknowledged.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> AddNoteAsync(Guid operatorUserId, Guid id, string text, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        (AdminResult? refusal, Grievance? g) = await OpenForActionAsync(operatorUserId, id, cancellationToken, allowClosed: true).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        string note = text.Trim();
        if (note.Length < 3)
        {
            return AdminResult.Refused("Write the note first.");
        }

        g!.Entries.Add(new GrievanceEntry { At = _clock.UtcNow, OperatorUserId = operatorUserId, Kind = "note", Text = note.Length > 4000 ? note[..4000] : note });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.GrievanceNote, operatorUserId, g, ipAddress, cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok("Note added.");
    }

    /// <inheritdoc />
    public async Task<AdminResult> CloseAsync(Guid operatorUserId, Guid id, GrievanceStatus outcome, string resolution, bool sendEmail, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (outcome is not GrievanceStatus.Resolved and not GrievanceStatus.Declined)
        {
            return AdminResult.Refused("Choose whether it was resolved or declined.");
        }

        (AdminResult? refusal, Grievance? g) = await OpenForActionAsync(operatorUserId, id, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        string answer = resolution.Trim();
        if (answer.Length < 20)
        {
            return AdminResult.Refused("Write the answer given to the person, in full.");
        }

        DateTimeOffset now = _clock.UtcNow;
        bool emailed = sendEmail && await SendAsync(MessageTemplateKinds.GrievanceResolution, g!, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reference"] = g!.Reference,
            ["resolution"] = answer,
        }, cancellationToken).ConfigureAwait(false);
        g!.AcknowledgedAt ??= now;
        g.Status = outcome;
        g.ClosedAt = now;
        g.Resolution = answer.Length > 4000 ? answer[..4000] : answer;
        g.Entries.Add(new GrievanceEntry { At = now, OperatorUserId = operatorUserId, Kind = outcome == GrievanceStatus.Resolved ? "resolved" : "declined", Text = g.Resolution });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.GrievanceClose, operatorUserId, g, ipAddress, cancellationToken, ("outcome", outcome == GrievanceStatus.Resolved ? "resolved" : "declined"), ("emailed", emailed ? "true" : "false"), ("late", now > g.ResolveBy ? "true" : "false")).ConfigureAwait(false);
        return AdminResult.Ok(emailed ? "Closed, and the answer was e-mailed." : "The grievance was closed.");
    }

    private static GrievanceRow Row(Grievance g, DateTimeOffset now) => new(
        g.Id,
        g.Reference,
        g.ReceivedAt,
        g.Category,
        g.ComplainantName,
        g.Status,
        g.AcknowledgeBy,
        g.ResolveBy,
        g.AcknowledgedAt is null && g.ClosedAt is null && now > g.AcknowledgeBy,
        g.ClosedAt is null && now > g.ResolveBy);

    private async Task<bool> SendAsync(string kind, Grievance g, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (!g.ComplainantContact.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        string? locale = g.UserId is Guid userId
            ? await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Locale).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        values["name"] = g.ComplainantName;
        try
        {
            await _email.SendAsync(await _templates.EmailAsync(kind, locale ?? "en-IN", null, null, values, g.ComplainantContact, g.ComplainantName, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<(AdminResult? Refusal, Grievance? Grievance)> OpenForActionAsync(Guid operatorUserId, Guid id, CancellationToken cancellationToken, bool allowClosed = false)
    {
        if (await RequireActAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is AdminResult refusal)
        {
            return (refusal, null);
        }

        Grievance? g = await _db.Grievances.Include(x => x.Entries).SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        if (g is null)
        {
            return (AdminResult.Refused("No such grievance."), null);
        }

        return !allowClosed && g.ClosedAt is not null ? (AdminResult.Refused("It is already closed."), null) : (null, g);
    }

    private async Task<AdminResult?> RequireActAsync(Guid operatorUserId, CancellationToken cancellationToken)
    {
        PlatformRole? role = await RoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        return role is null
            ? AdminResult.Refused("You do not have console access.")
            : role < PlatformRole.Support ? AdminResult.Refused($"That needs {PlatformRanks.Label(PlatformRole.Support)} access.") : null;
    }

    private Task<PlatformRole?> RoleAsync(Guid operatorUserId, CancellationToken cancellationToken)
        => _db.PlatformOperators.AsNoTracking()
            .Where(o => o.UserId == operatorUserId && o.RevokedAt == null)
            .Select(o => (PlatformRole?)o.Role)
            .FirstOrDefaultAsync(cancellationToken);

    private Task AuditAsync(string action, Guid operatorUserId, Grievance g, string? ipAddress, CancellationToken cancellationToken, params (string Key, string Value)[] extra)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal) { ["reference"] = g.Reference };
        foreach ((string key, string value) in extra)
        {
            metadata[key] = value;
        }

        return _audit.WriteAsync(
            new AuditEntry(action, AuditActorType.Admin, operatorUserId, TargetType: "grievance", TargetId: g.Id, Metadata: JsonSerializer.Serialize(metadata), IpAddress: ipAddress),
            cancellationToken);
    }
}
