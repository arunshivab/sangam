using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary><see cref="IJoinCodeService"/> over the identity database (rc.12, 4.1).</summary>
public sealed class EfJoinCodeService : IJoinCodeService
{
    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfJoinCodeService(SangamDbContext db, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<JoinCodeIssued> CreateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        await EndLiveAsync(userId, now, cancellationToken).ConfigureAwait(false);

        // A clash with another person's live code is astronomically unlikely, but a code must name one account only.
        string code;
        string hash;
        do
        {
            code = StaffCodes.New();
            hash = StaffCodes.Hash(StaffCodes.Normalise(code));
        }
        while (await _db.JoinCodes.AnyAsync(j => j.CodeHash == hash && j.UsedAt == null && j.ExpiresAt > now, cancellationToken).ConfigureAwait(false));

        JoinCode row = new() { Id = Guid.NewGuid(), UserId = userId, CodeHash = hash, CreatedAt = now, ExpiresAt = now + JoinCode.Lifetime };
        _db.JoinCodes.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserJoinCodeCreate, AuditActorType.User, userId, TargetType: "user", TargetId: userId), cancellationToken).ConfigureAwait(false);
        return new JoinCodeIssued(code, row.ExpiresAt, Qr(code));
    }

    /// <inheritdoc />
    public async Task CancelAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EndLiveAsync(userId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EndLiveAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (JoinCode live in await _db.JoinCodes.Where(j => j.UserId == userId && j.UsedAt == null && j.ExpiresAt > now).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            live.ExpiresAt = now;
        }
    }

    /// <summary>The code as an inline SVG, made on this server.</summary>
    private static string Qr(string code)
    {
        using QRCoder.QRCodeGenerator generator = new();
        using QRCoder.QRCodeData data = generator.CreateQrCode(code, QRCoder.QRCodeGenerator.ECCLevel.Q);
        using QRCoder.SvgQRCode svg = new(data);
        return svg.GetGraphic(6, "#15302E", "#FFFDF8", drawQuietZones: true);
    }
}
