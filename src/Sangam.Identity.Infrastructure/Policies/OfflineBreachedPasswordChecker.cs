using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Security;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// The breached-password check against the offline list (D-J; <see cref="PwnedPasswordList"/>). It runs only
/// when switched on (<c>Sangam:Passwords:BreachCheck:Enabled</c>) and a usable list is at
/// <c>Sangam:Passwords:BreachCheck:ListPath</c>; until then it is off, and the monitoring page says why. A list
/// replaced by a refresh is picked up within a minute, without a restart. Nothing leaves the server.
/// </summary>
public sealed partial class OfflineBreachedPasswordChecker : IBreachedPasswordChecker, IBreachListStatus
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);

    private readonly PolicySettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<OfflineBreachedPasswordChecker> _logger;
    private readonly Lock _gate = new();
    private Loaded? _list;
    private string? _problem;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

    /// <summary>Initialises the checker.</summary>
    /// <param name="settings">Settings (switch and list path).</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    public OfflineBreachedPasswordChecker(PolicySettings settings, IClock clock, ILogger<OfflineBreachedPasswordChecker> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool Available => _settings.BreachCheckEnabled && Current() is not null;

    /// <inheritdoc />
    public BreachListStatus Status
    {
        get
        {
            Loaded? list = Current();
            string? problem = _settings.BreachListPath is null ? "No list path is set (Sangam:Passwords:BreachCheck:ListPath)." : _problem;
            return new BreachListStatus(_settings.BreachCheckEnabled, list is not null, list?.Header.ListDate, list?.Header.Entries ?? 0, list is null ? problem : null);
        }
    }

    /// <inheritdoc />
    public Task<bool?> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!_settings.BreachCheckEnabled || Current() is not Loaded list)
        {
            return Task.FromResult<bool?>(null);
        }

        try
        {
            return Task.FromResult<bool?>(PwnedPasswordList.Contains(list.Path, list.Header, PwnedPasswordList.Key(password)));
        }
        catch (IOException ex)
        {
            LogUnreadable(ex.GetType().Name);
            return Task.FromResult<bool?>(null);
        }
    }

    private Loaded? Current()
    {
        string? path = _settings.BreachListPath;
        if (path is null)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        lock (_gate)
        {
            if (now - _checkedAt < RecheckEvery)
            {
                return _list;
            }

            _checkedAt = now;
            FileInfo file = new(path);
            if (_list is not null && file.Exists && file.LastWriteTimeUtc == _list.Written && file.Length == _list.Length)
            {
                return _list;
            }

            try
            {
                (PwnedPasswordList.Header? header, string? problem) = PwnedPasswordList.ReadHeader(path);
                _problem = problem;
                _list = header is null ? null : new Loaded(path, header, file.LastWriteTimeUtc, file.Length);
                if (header is not null)
                {
                    LogLoaded(header.Entries, header.ListDate);
                }
            }
            catch (IOException ex)
            {
                _problem = "The list file could not be read (" + ex.GetType().Name + ").";
                _list = null;
            }

            return _list;
        }
    }

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "Breached-password list loaded: {Entries} hashes, downloaded {ListDate}")]
    private partial void LogLoaded(long entries, DateOnly? listDate);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning, Message = "The breached-password list could not be read ({Error}); the password was not checked")]
    private partial void LogUnreadable(string error);

    private sealed record Loaded(string Path, PwnedPasswordList.Header Header, DateTime Written, long Length);
}
