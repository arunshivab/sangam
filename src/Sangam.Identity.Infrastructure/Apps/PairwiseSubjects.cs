using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Apps;

/// <summary>
/// rc.14 (decision record 0017): the id each application knows each person by. A new id is a random UUID (PostgreSQL's <c>gen_random_uuid()</c>), written with
/// <c>ON CONFLICT DO NOTHING</c> so two requests at once agree on one. Sangam's own applications (<see cref="App.IsPlatform"/>)
/// use the person's Sangam id and get no rows.
/// </summary>
public static class PairwiseSubjects
{
    /// <summary>The id <paramref name="appId"/> knows <paramref name="userId"/> by, given out now if it has none.</summary>
    /// <param name="db">The context.</param>
    /// <param name="appId">The application.</param>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<Guid> ForAsync(SangamDbContext db, Guid appId, Guid userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, Guid> found = await ForManyAsync(db, appId, [userId], cancellationToken).ConfigureAwait(false);
        return found[userId];
    }

    /// <summary>The ids <paramref name="appId"/> knows several people by, given out now where it has none.</summary>
    /// <param name="db">The context.</param>
    /// <param name="appId">The application.</param>
    /// <param name="userIds">The people.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sangam id → the application's id.</returns>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> ForManyAsync(SangamDbContext db, Guid appId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(userIds);
        List<Guid> people = [.. userIds.Distinct()];
        if (people.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        if (await IsPlatformAsync(db, appId, cancellationToken).ConfigureAwait(false))
        {
            return people.ToDictionary(p => p, p => p);
        }

        Dictionary<Guid, Guid> known = await db.AppSubjects.AsNoTracking()
            .Where(s => s.AppId == appId && people.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.Subject, cancellationToken).ConfigureAwait(false);
        List<Guid> missing = [.. people.Where(p => !known.ContainsKey(p))];
        if (missing.Count > 0)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid[] adding = [.. missing];
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO app_subjects (app_id, user_id, subject, created_at) SELECT {appId}, u, gen_random_uuid(), {now} FROM unnest({adding}) AS u ON CONFLICT (app_id, user_id) DO NOTHING",
                cancellationToken).ConfigureAwait(false);

            Dictionary<Guid, Guid> added = await db.AppSubjects.AsNoTracking()
                .Where(s => s.AppId == appId && missing.Contains(s.UserId))
                .ToDictionaryAsync(s => s.UserId, s => s.Subject, cancellationToken).ConfigureAwait(false);
            foreach ((Guid person, Guid subject) in added)
            {
                known[person] = subject;
            }
        }

        return known;
    }

    /// <summary>The person an application's id names, or <see langword="null"/>.</summary>
    /// <param name="db">The context.</param>
    /// <param name="appId">The application.</param>
    /// <param name="subject">The id the application sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<Guid?> UserOfAsync(SangamDbContext db, Guid appId, Guid subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await IsPlatformAsync(db, appId, cancellationToken).ConfigureAwait(false))
        {
            return subject;
        }

        return await db.AppSubjects.AsNoTracking()
            .Where(s => s.AppId == appId && s.Subject == subject)
            .Select(s => (Guid?)s.UserId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The people several of an application's ids name; ids it does not know are left out.</summary>
    /// <param name="db">The context.</param>
    /// <param name="appId">The application.</param>
    /// <param name="subjects">The ids the application sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application's id → Sangam id.</returns>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> UsersOfAsync(SangamDbContext db, Guid appId, IReadOnlyCollection<Guid> subjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(subjects);
        List<Guid> ids = [.. subjects.Distinct()];
        if (await IsPlatformAsync(db, appId, cancellationToken).ConfigureAwait(false))
        {
            return ids.ToDictionary(s => s, s => s);
        }

        return await db.AppSubjects.AsNoTracking()
            .Where(s => s.AppId == appId && ids.Contains(s.Subject))
            .ToDictionaryAsync(s => s.Subject, s => s.UserId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The person's sign-in session (going or ended) that an application's <c>sid</c> names.</summary>
    /// <param name="db">The context.</param>
    /// <param name="appId">The application.</param>
    /// <param name="userId">The person.</param>
    /// <param name="appSessionId">The <c>sid</c> the application has.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<Guid?> SessionOfAsync(SangamDbContext db, Guid appId, Guid userId, Guid appSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        bool platform = await IsPlatformAsync(db, appId, cancellationToken).ConfigureAwait(false);
        // Ended sessions too, so the caller can answer "that sign-in has already ended" rather than "no such sign-in".
        List<Guid> sessions = await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (Guid session in sessions)
        {
            if (AppSessionIds.For(session, appId, platform) == appSessionId)
            {
                return session;
            }
        }

        return platform ? appSessionId : null;
    }

    /// <summary>Whether the application is one of Sangam's own.</summary>
    /// <param name="db">The context.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<bool> IsPlatformAsync(SangamDbContext db, Guid appId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Apps.AsNoTracking().AnyAsync(a => a.Id == appId && a.IsPlatform, cancellationToken);
    }
}

/// <summary>rc.14: <see cref="ISubjectIds"/> over <see cref="PairwiseSubjects"/>.</summary>
internal sealed class EfSubjectIds : ISubjectIds
{
    private readonly SangamDbContext _db;

    public EfSubjectIds(SangamDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public Task<Guid> SubjectAsync(Guid appId, Guid userId, CancellationToken cancellationToken = default)
        => PairwiseSubjects.ForAsync(_db, appId, userId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, Guid>> SubjectsAsync(Guid appId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
        => PairwiseSubjects.ForManyAsync(_db, appId, userIds, cancellationToken);

    public Task<Guid?> UserAsync(Guid appId, Guid subject, CancellationToken cancellationToken = default)
        => PairwiseSubjects.UserOfAsync(_db, appId, subject, cancellationToken);

    public Task<Guid?> SessionAsync(Guid appId, Guid userId, Guid appSessionId, CancellationToken cancellationToken = default)
        => PairwiseSubjects.SessionOfAsync(_db, appId, userId, appSessionId, cancellationToken);
}
