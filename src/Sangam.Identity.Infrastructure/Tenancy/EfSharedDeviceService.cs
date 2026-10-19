using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Devices;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Portal;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary>
/// rc.12 (T1): shared ward devices on PostgreSQL. The device key and the enrolment token are kept only as SHA-256 hashes; the
/// PIN is hashed like a password. A lock is a mark on the session row: the cookie stays in the browser, but the authorization
/// endpoint will not go on until the person unlocks or signs in again.
/// </summary>
public sealed class EfSharedDeviceService : ISharedDeviceService
{
    /// <summary>Wrong PINs before the PIN is cancelled.</summary>
    public const int MaxWrongPins = 5;

    /// <summary>The most tiles on "Who's using this tablet?".</summary>
    public const int MaxTiles = 6;

    /// <summary>How long an enrolment link works.</summary>
    public static readonly TimeSpan EnrolLifetime = TimeSpan.FromMinutes(15);

    private static readonly PasswordHasher<UserPin> PinHasher = new();

    // The shortest an organisation may make the rules.
    private const int MinPinMinutes = 5;
    private const int MinFullSignInHours = 1;
    private readonly SangamDbContext _db;
    private readonly StaffRights _rights;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="rights">Who manages what.</param>
    /// <param name="audit">Audit trail.</param>
    /// <param name="clock">Clock.</param>
    public EfSharedDeviceService(SangamDbContext db, StaffRights rights, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rights = rights ?? throw new ArgumentNullException(nameof(rights));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Whether a PIN is six digits and not trivially guessed (all the same, or a run up or down).</summary>
    /// <param name="pin">The PIN.</param>
    /// <returns><see langword="true"/> when acceptable.</returns>
    public static bool IsAcceptablePin(string? pin)
    {
        if (pin is null || pin.Length != 6 || !pin.All(char.IsAsciiDigit))
        {
            return false;
        }

        bool same = pin.All(c => c == pin[0]);
        bool up = true;
        bool down = true;
        for (int i = 1; i < pin.Length; i++)
        {
            up &= pin[i] == pin[i - 1] + 1;
            down &= pin[i] == pin[i - 1] - 1;
        }

        return !same && !up && !down;
    }

    /// <inheritdoc />
    public async Task<DeviceEnrolStart> StartEnrolAsync(Guid appId, Guid adminUserId, Guid orgId, string label, CancellationToken cancellationToken = default)
    {
        string name = (label ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 100)
        {
            return new DeviceEnrolStart(DeviceEnrolStatus.LabelRequired);
        }

        Organisation? org = await _rights.OrganisationAsync(appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || org.ClosedAt is not null || !await _rights.ManagesAsync(appId, org, adminUserId, cancellationToken).ConfigureAwait(false))
        {
            return new DeviceEnrolStart(DeviceEnrolStatus.NotAdmin);
        }

        DateTimeOffset now = _clock.UtcNow;
        string token = StaffCodes.NewToken();
        SharedDevice device = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            OrgId = orgId,
            Label = name,
            EnrolTokenHash = StaffCodes.Hash(token),
            EnrolExpiresAt = now + EnrolLifetime,
            EnrolledByUserId = adminUserId,
            CreatedAt = now,
        };
        _db.SharedDevices.Add(device);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.DeviceEnrolStart, AuditActorType.Admin, adminUserId, appId, "device", device.Id,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["organisation"] = orgId, ["label"] = name })),
            cancellationToken).ConfigureAwait(false);
        return new DeviceEnrolStart(DeviceEnrolStatus.Started, token);
    }

    /// <inheritdoc />
    public async Task<DeviceEnrolled?> CompleteEnrolAsync(string token, Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string hash = StaffCodes.Hash(token.Trim());
        DateTimeOffset now = _clock.UtcNow;
        SharedDevice? device = await _db.SharedDevices
            .FirstOrDefaultAsync(d => d.EnrolTokenHash == hash && d.EnrolExpiresAt > now && d.EnrolledAt == null && d.RevokedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (device is null || device.EnrolledByUserId != userId)
        {
            return null;
        }

        string key = StaffCodes.NewToken();
        device.KeyHash = StaffCodes.Hash(key);
        device.EnrolledAt = now;
        device.EnrolTokenHash = null;
        device.EnrolExpiresAt = null;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.DeviceEnrol, AuditActorType.Admin, userId, device.AppId, "device", device.Id), cancellationToken).ConfigureAwait(false);
        return new DeviceEnrolled(device.Id, device.Label, key);
    }

    /// <inheritdoc />
    public async Task<SharedDeviceInfo?> FindAsync(string? key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            return null;
        }

        string hash = StaffCodes.Hash(key);
        var found = await _db.SharedDevices.AsNoTracking()
            .Where(d => d.KeyHash == hash && d.RevokedAt == null && d.EnrolledAt != null)
            .Join(_db.Organisations.Where(o => o.DeletedAt == null && o.ClosedAt == null), d => d.OrgId, o => o.Id, (d, o) => new { Device = d, o.Name, o.Path })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        DeviceRules rules = await RulesOnPathAsync(OrganisationPath.Ids(found.Path), cancellationToken).ConfigureAwait(false);
        return new SharedDeviceInfo(found.Device.Id, found.Device.AppId, found.Device.OrgId, found.Device.Label, found.Name, rules);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SharedDeviceRow>> ListAsync(Guid appId, Guid adminUserId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await _rights.OrganisationAsync(appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || !await _rights.ManagesAsync(appId, org, adminUserId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        DateTimeOffset now = _clock.UtcNow;
        string path = org.Path;
        var rows = await _db.SharedDevices.AsNoTracking()
            .Where(d => d.AppId == appId && d.RevokedAt == null && (d.EnrolledAt != null || d.EnrolExpiresAt > now))
            .Join(_db.Organisations.Where(o => o.Path.StartsWith(path)), d => d.OrgId, o => o.Id, (d, o) => new { d, o.Name })
            .Join(_db.Users, x => x.d.EnrolledByUserId, u => u.Id, (x, u) => new
            {
                x.d.Id,
                x.d.Label,
                OrgName = x.Name,
                u.FirstName,
                u.LastName,
                x.d.EnrolledAt,
                x.d.LastSeenAt,
                x.d.CreatedAt,
                People = _db.SharedDevicePeople.Count(p => p.DeviceId == x.d.Id),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows
            .OrderByDescending(r => r.LastSeenAt ?? r.EnrolledAt ?? r.CreatedAt)
            .Select(r => new SharedDeviceRow(r.Id, r.Label, r.OrgName, (r.FirstName + " " + r.LastName).Trim(), r.EnrolledAt, r.LastSeenAt, r.People))];
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(Guid appId, Guid adminUserId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        SharedDevice? device = await _db.SharedDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.AppId == appId && d.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        Organisation? org = device is null ? null : await _rights.OrganisationAsync(appId, device.OrgId, cancellationToken).ConfigureAwait(false);
        if (device is null || org is null || !await _rights.ManagesAsync(appId, org, adminUserId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        device.RevokedAt = now;
        device.RevokedByUserId = adminUserId;
        device.KeyHash = null;
        device.EnrolTokenHash = null;

        // Lost or stolen: every session still open on it ends now.
        List<UserSession> open = await _db.UserSessions.Where(s => s.SharedDeviceId == deviceId && s.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (UserSession session in open)
        {
            session.RevokedAt = now;
            session.RevokedReason = "device_removed";
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await LogoutQueue.EnqueueAsync(_db, [.. open.Select(s => s.Id)], now, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.DeviceRemove, AuditActorType.Admin, adminUserId, appId, "device", deviceId,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["sessions_ended"] = open.Count })),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<DeviceRules> RulesAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        string? path = await _db.Organisations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Path).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return path is null ? DeviceRules.Sangam : await RulesOnPathAsync(OrganisationPath.Ids(path), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeviceRulesHere?> RulesHereAsync(Guid appId, Guid adminUserId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await _rights.OrganisationAsync(appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || !await _rights.ManagesAsync(appId, org, adminUserId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        IReadOnlyList<Guid> ids = OrganisationPath.Ids(org.Path);
        DeviceRules limit = await RulesOnPathAsync([.. ids.Where(id => id != org.Id)], cancellationToken).ConfigureAwait(false);
        DeviceRules rules = await RulesOnPathAsync(ids, cancellationToken).ConfigureAwait(false);
        return new DeviceRulesHere(rules, limit, org.DevicePinMinutes is not null || org.DeviceFullSignInHours is not null);
    }

    /// <inheritdoc />
    public async Task<bool> SetRulesAsync(Guid appId, Guid adminUserId, Guid orgId, DeviceRules rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Organisation? org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == orgId && o.RegisteredViaAppId == appId && o.DeletedAt == null, cancellationToken).ConfigureAwait(false);
        if (org is null || !await _rights.ManagesAsync(appId, org, adminUserId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // rc.13: never longer than what holds above; the same as above is not stored, so it keeps following them.
        DeviceRules limit = await RulesOnPathAsync([.. OrganisationPath.Ids(org.Path).Where(id => id != org.Id)], cancellationToken).ConfigureAwait(false);
        DeviceRules kept = new(
            Math.Clamp(rules.PinMinutes, MinPinMinutes, limit.PinMinutes),
            Math.Clamp(rules.FullSignInHours, MinFullSignInHours, limit.FullSignInHours));
        org.DevicePinMinutes = kept.PinMinutes >= limit.PinMinutes ? null : kept.PinMinutes;
        org.DeviceFullSignInHours = kept.FullSignInHours >= limit.FullSignInHours ? null : kept.FullSignInHours;
        org.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.DeviceRules, AuditActorType.Admin, adminUserId, appId, "organisation", orgId,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["pin_minutes"] = kept.PinMinutes, ["full_sign_in_hours"] = kept.FullSignInHours, ["set_here"] = org.DevicePinMinutes is not null || org.DeviceFullSignInHours is not null })),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The shortest rules set on any of the organisations, never longer than Sangam's.</summary>
    private async Task<DeviceRules> RulesOnPathAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        List<Guid> list = [.. ids];
        if (list.Count == 0)
        {
            return DeviceRules.Sangam;
        }

        var stored = await _db.Organisations.AsNoTracking()
            .Where(o => list.Contains(o.Id) && o.DeletedAt == null)
            .Select(o => new { o.DevicePinMinutes, o.DeviceFullSignInHours })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int pin = stored.Select(o => o.DevicePinMinutes).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(DeviceRules.Sangam.PinMinutes).Min();
        int full = stored.Select(o => o.DeviceFullSignInHours).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(DeviceRules.Sangam.FullSignInHours).Min();
        return Stricter(pin, full);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceTile>> TilesAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        SharedDevice? device = await _db.SharedDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        string? devicePath = device is null ? null : await _db.Organisations.AsNoTracking().Where(o => o.Id == device.OrgId).Select(o => o.Path).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (device is null || devicePath is null)
        {
            return [];
        }

        DateTimeOffset now = _clock.UtcNow;
        Guid appId = device.AppId;
        string customerPath = OrganisationPath.ForRoot(OrganisationPath.CustomerId(devicePath));

        // Only that hospital's staff: people with a live place somewhere in the customer's tree.
        var people = await _db.SharedDevicePeople.AsNoTracking()
            .Where(p => p.DeviceId == deviceId)
            .Where(p => _db.OrgMemberships.Any(m => m.UserId == p.UserId && m.AppId == appId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now)
                && _db.Organisations.Any(o => o.Id == m.OrgId && o.Path.StartsWith(customerPath) && o.DeletedAt == null)))
            .OrderByDescending(p => p.LastSeenAt)
            .Take(MaxTiles)
            .Join(_db.Users.Where(u => u.Status == UserStatus.Active), p => p.UserId, u => u.Id, (p, u) => new { Person = p, u.FirstName, u.LastName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (people.Count == 0)
        {
            return [];
        }

        List<Guid> userIds = [.. people.Select(p => p.Person.UserId)];
        var roles = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.OrgId == device.OrgId && userIds.Contains(m.UserId) && m.RevokedAt == null && m.RoleId != null)
            .Select(m => new { m.UserId, m.Role!.DisplayName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<Guid> unlockable = await UnlockableAsync(device, [.. people.Select(p => p.Person)], false, cancellationToken).ConfigureAwait(false);

        return [.. people
            .OrderByDescending(p => p.Person.LastSeenAt)
            .Select(p => new DeviceTile(
                p.Person.Id,
                (p.FirstName + " " + p.LastName).Trim(),
                roles.Where(r => r.UserId == p.Person.UserId).Select(r => r.DisplayName).OrderBy(r => r, StringComparer.Ordinal).FirstOrDefault(),
                unlockable.Contains(p.Person.Id)))];
    }

    /// <inheritdoc />
    public async Task<(Guid UserId, string Name)?> TileAsync(Guid deviceId, Guid tileId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DeviceTile> tiles = await TilesAsync(deviceId, cancellationToken).ConfigureAwait(false);
        DeviceTile? tile = tiles.FirstOrDefault(t => t.Id == tileId);
        if (tile is null)
        {
            return null;
        }

        Guid userId = await _db.SharedDevicePeople.AsNoTracking().Where(p => p.Id == tileId).Select(p => p.UserId).SingleAsync(cancellationToken).ConfigureAwait(false);
        return (userId, tile.Name);
    }

    /// <inheritdoc />
    public async Task SignedInAsync(Guid deviceId, DeviceSignIn signIn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        SharedDevice? device = await _db.SharedDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        DateTimeOffset now = _clock.UtcNow;
        SharedDevicePerson? person = await _db.SharedDevicePeople.FirstOrDefaultAsync(p => p.DeviceId == deviceId && p.UserId == signIn.UserId, cancellationToken).ConfigureAwait(false);
        if (person is null)
        {
            person = new SharedDevicePerson { Id = Guid.NewGuid(), DeviceId = deviceId, UserId = signIn.UserId };
            _db.SharedDevicePeople.Add(person);
        }

        person.SessionId = signIn.SessionId;
        person.LastFullSignInAt = now;
        person.LastSeenAt = now;
        person.SignInMode = signIn.Mode;
        person.Methods = string.Join(' ', signIn.Methods);
        person.SecondFactor = signIn.SecondFactor;
        person.LockedAt = null;
        device.LastSeenAt = now;

        UserSession? session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Id == signIn.SessionId, cancellationToken).ConfigureAwait(false);
        session?.SharedDeviceId = deviceId;

        // A full sign-in gives a cancelled PIN a fresh start.
        UserPin? pin = await _db.UserPins.FirstOrDefaultAsync(p => p.UserId == signIn.UserId, cancellationToken).ConfigureAwait(false);
        if (pin is not null)
        {
            pin.FailedCount = 0;
            pin.CancelledAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeviceGate> GateAsync(Guid sessionId, Guid? deviceId, CancellationToken cancellationToken = default)
    {
        UserSession? session = await _db.UserSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return DeviceGate.None;
        }

        bool here = deviceId is not null && session.SharedDeviceId == deviceId;
        SharedDevicePerson? person = here
            ? await _db.SharedDevicePeople.AsNoTracking().FirstOrDefaultAsync(p => p.DeviceId == deviceId && p.UserId == session.UserId, cancellationToken).ConfigureAwait(false)
            : null;
        SharedDevice? device = here ? await _db.SharedDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, cancellationToken).ConfigureAwait(false) : null;

        if (session.LockedAt is not null)
        {
            if (device is null || person is null || person.SessionId != sessionId)
            {
                return DeviceGate.SignInAgain;
            }

            return (await UnlockableAsync(device, [person], false, cancellationToken).ConfigureAwait(false)).Contains(person.Id) ? DeviceGate.Unlock : DeviceGate.SignInAgain;
        }

        if (device is null || person is null)
        {
            return DeviceGate.None;
        }

        DeviceRules rules = await RulesForAsync(device, cancellationToken).ConfigureAwait(false);
        if (_clock.UtcNow - person.LastFullSignInAt > TimeSpan.FromHours(rules.FullSignInHours))
        {
            return DeviceGate.SignInAgain;
        }

        return person.PinOfferedSessionId != sessionId && !await HasPinAsync(session.UserId, cancellationToken).ConfigureAwait(false)
            ? DeviceGate.OfferPin
            : DeviceGate.None;
    }

    /// <inheritdoc />
    public async Task PinOfferedAsync(Guid deviceId, Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
        => await _db.SharedDevicePeople
            .Where(p => p.DeviceId == deviceId && p.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.PinOfferedSessionId, sessionId), cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> SetPinAsync(Guid userId, string pin, CancellationToken cancellationToken = default)
    {
        if (!IsAcceptablePin(pin))
        {
            return false;
        }

        UserPin? stored = await _db.UserPins.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            stored = new UserPin { UserId = userId };
            _db.UserPins.Add(stored);
        }

        stored.PinHash = PinHasher.HashPassword(stored, pin);
        stored.SetAt = _clock.UtcNow;
        stored.FailedCount = 0;
        stored.CancelledAt = null;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.UserPinSet, AuditActorType.User, userId, TargetType: "user", TargetId: userId), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> HasPinAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.UserPins.AsNoTracking().AnyAsync(p => p.UserId == userId && p.CancelledAt == null, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> LockAsync(Guid sessionId, Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        UserSession? session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        bool already = session.LockedAt is not null;
        if (!already)
        {
            session.LockedAt = now;
            await _db.SharedDevicePeople
                .Where(p => p.SessionId == sessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.LockedAt, now), cancellationToken)
                .ConfigureAwait(false);

            // One person, one device, one state: the person's other applications in this session lock too.
            List<Guid> others = await _db.SessionApps.AsNoTracking()
                .Where(sa => sa.SessionId == sessionId && sa.AppId != appId)
                .Select(sa => sa.AppId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (Guid other in others)
            {
                await Provisioning.AppEventLog.AddAsync(_db, AppEventTypes.SessionLocked, other, userId, null, new Dictionary<string, object?> { ["sid"] = sessionId }, now, cancellationToken).ConfigureAwait(false);
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.SessionLock, AuditActorType.Api, userId, appId, "session", sessionId,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["shared_device"] = session.SharedDeviceId is not null, ["already_locked"] = already })),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<Guid?> LockedTileAsync(Guid deviceId, Guid sessionId, CancellationToken cancellationToken = default)
        => _db.SharedDevicePeople.AsNoTracking()
            .Where(p => p.DeviceId == deviceId && p.SessionId == sessionId && p.LockedAt != null)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<PinUnlockResult> UnlockAsync(Guid deviceId, Guid tileId, string pin, CancellationToken cancellationToken = default)
    {
        SharedDevice? device = await _db.SharedDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        SharedDevicePerson? person = device is null ? null : await _db.SharedDevicePeople.FirstOrDefaultAsync(p => p.Id == tileId && p.DeviceId == deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || person is null || !(await UnlockableAsync(device, [person], true, cancellationToken).ConfigureAwait(false)).Contains(person.Id))
        {
            return new PinUnlockResult(PinUnlockStatus.SignInAgain);
        }

        UserPin? stored = await _db.UserPins.FirstOrDefaultAsync(p => p.UserId == person.UserId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return new PinUnlockResult(PinUnlockStatus.NoPin);
        }

        if (stored.CancelledAt is not null)
        {
            return new PinUnlockResult(PinUnlockStatus.Cancelled);
        }

        DateTimeOffset now = _clock.UtcNow;
        string typed = (pin ?? string.Empty).Trim();
        if (typed.Length != 6 || PinHasher.VerifyHashedPassword(stored, stored.PinHash, typed) == PasswordVerificationResult.Failed)
        {
            stored.FailedCount++;
            bool cancelled = stored.FailedCount >= MaxWrongPins;
            if (cancelled)
            {
                stored.CancelledAt = now;
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(
                new AuditEntry(cancelled ? AuditActions.UserPinCancel : AuditActions.DeviceUnlockFailed, AuditActorType.User, person.UserId, device.AppId, "device", deviceId,
                    JsonSerializer.Serialize(new Dictionary<string, object> { ["wrong"] = stored.FailedCount })),
                cancellationToken).ConfigureAwait(false);
            return cancelled ? new PinUnlockResult(PinUnlockStatus.Cancelled) : new PinUnlockResult(PinUnlockStatus.Wrong, TriesLeft: MaxWrongPins - stored.FailedCount);
        }

        UserSession session = await _db.UserSessions.SingleAsync(s => s.Id == person.SessionId, cancellationToken).ConfigureAwait(false);
        stored.FailedCount = 0;
        session.LockedAt = null;
        session.LastSeenAt = now;
        person.LockedAt = null;
        person.LastSeenAt = now;
        device.LastSeenAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.DeviceUnlock, AuditActorType.User, person.UserId, device.AppId, "device", deviceId), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> methods = person.Methods.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new PinUnlockResult(PinUnlockStatus.Unlocked, new DeviceResume(person.UserId, session.Id, person.SignInMode, methods, person.SecondFactor, person.LastFullSignInAt));
    }

    private static DeviceRules Stricter(int? pinMinutes, int? fullSignInHours)
        => new(
            Math.Clamp(pinMinutes ?? DeviceRules.Sangam.PinMinutes, MinPinMinutes, DeviceRules.Sangam.PinMinutes),
            Math.Clamp(fullSignInHours ?? DeviceRules.Sangam.FullSignInHours, MinFullSignInHours, DeviceRules.Sangam.FullSignInHours));

    // rc.13: the device's own organisation decides, through the shortest rules at it or above.
    private Task<DeviceRules> RulesForAsync(SharedDevice device, CancellationToken cancellationToken) => RulesAsync(device.OrgId, cancellationToken);

    /// <summary>
    /// The people on the device whose locked session a PIN may unlock now: locked within the PIN window, signed in fully
    /// within the full sign-in window, the session still open and on this device, and (unless asked to ignore it) a PIN that
    /// works.
    /// </summary>
    private async Task<HashSet<Guid>> UnlockableAsync(SharedDevice device, IReadOnlyList<SharedDevicePerson> people, bool ignorePinState, CancellationToken cancellationToken)
    {
        List<SharedDevicePerson> locked = [.. people.Where(p => p.LockedAt is not null && p.SessionId is not null)];
        if (locked.Count == 0)
        {
            return [];
        }

        DeviceRules rules = await RulesForAsync(device, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _clock.UtcNow;
        List<Guid> sessionIds = [.. locked.Select(p => p.SessionId!.Value)];
        List<Guid> userIds = [.. locked.Select(p => p.UserId)];
        HashSet<Guid> open = [.. await _db.UserSessions.AsNoTracking()
            .Where(s => sessionIds.Contains(s.Id) && s.RevokedAt == null && s.LockedAt != null && s.SharedDeviceId == device.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)];
        HashSet<Guid> withPin = ignorePinState
            ? [.. userIds]
            : [.. await _db.UserPins.AsNoTracking().Where(p => userIds.Contains(p.UserId) && p.CancelledAt == null).Select(p => p.UserId).ToListAsync(cancellationToken).ConfigureAwait(false)];
        return [.. locked
            .Where(p => open.Contains(p.SessionId!.Value) && withPin.Contains(p.UserId)
                && now - p.LockedAt!.Value <= TimeSpan.FromMinutes(rules.PinMinutes)
                && now - p.LastFullSignInAt <= TimeSpan.FromHours(rules.FullSignInHours))
            .Select(p => p.Id)];
    }
}
