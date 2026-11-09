using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Insights;

/// <summary>
/// Logs every movie and episode each user watches to <see cref="IPlaybackHistory"/>: when, for how long (not counting
/// pauses), how far and on which app / device. Running sessions are saved every minute so a server restart loses
/// little; sessions whose app stopped reporting are closed after <see cref="StaleAfter"/>.
/// </summary>
public sealed class PlaybackTracker : IHostedService, IDisposable
{
    /// <summary>
    /// How long a session may go without a progress report before it's closed.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    // Apps report progress about every 10 s; a longer gap (e.g. the device slept) only counts this much.
    private static readonly TimeSpan _maxGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _saveEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _sweepEvery = TimeSpan.FromMinutes(5);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaybackHistory _history;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlaybackTracker> _logger;
    private readonly ConcurrentDictionary<string, Tracked> _sessions = new();
    private ITimer? _sweepTimer;
    private DateTime _lastCleanup = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackTracker"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="history">The playback log.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackTracker(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IPlaybackHistory history,
        TimeProvider timeProvider,
        ILogger<PlaybackTracker> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _history = history;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sweepTimer = _timeProvider.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(30), _sweepEvery);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sweepTimer?.Dispose();
        _sweepTimer = null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryRemove(key, out var tracked))
            {
                Save(tracked, now, completed: false);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _sweepTimer?.Dispose();

    /// <summary>
    /// Handles a playback start report.
    /// </summary>
    /// <param name="e">The report.</param>
    internal void OnStart(PlaybackProgressEventArgs e)
    {
        if (e.Item is not (Movie or Episode))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var user in e.Users)
        {
            var key = Key(e, user.Id);

            // A new start of the same play session (e.g. after switching audio track) continues it.
            if (_sessions.ContainsKey(key))
            {
                continue;
            }

            var tracked = new Tracked(CreateRecord(e, user.Id, now)) { LastReportUtc = now, IsPaused = e.IsPaused };
            _sessions[key] = tracked;
            Save(tracked, now, completed: false);
        }
    }

    /// <summary>
    /// Handles a playback progress report.
    /// </summary>
    /// <param name="e">The report.</param>
    internal void OnProgress(PlaybackProgressEventArgs e)
    {
        if (e.Item is not (Movie or Episode))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var user in e.Users)
        {
            if (!_sessions.TryGetValue(Key(e, user.Id), out var tracked))
            {
                // Playback started before the server did: track it from now.
                OnStart(e);
                continue;
            }

            lock (tracked)
            {
                Advance(tracked, now);
                tracked.IsPaused = e.IsPaused;
                tracked.Record.PositionTicks = e.PlaybackPositionTicks ?? tracked.Record.PositionTicks;
                if (now - tracked.LastSavedUtc >= _saveEvery)
                {
                    Save(tracked, now, completed: false);
                }
            }
        }
    }

    /// <summary>
    /// Handles a playback stop report.
    /// </summary>
    /// <param name="e">The report.</param>
    internal void OnStopped(PlaybackStopEventArgs e)
    {
        if (e.Item is not (Movie or Episode))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var user in e.Users)
        {
            if (_sessions.TryRemove(Key(e, user.Id), out var tracked))
            {
                lock (tracked)
                {
                    Advance(tracked, now);
                    tracked.Record.PositionTicks = e.PlaybackPositionTicks ?? tracked.Record.PositionTicks;
                    Save(tracked, now, e.PlayedToCompletion);
                }
            }
        }
    }

    /// <summary>
    /// Closes sessions whose app stopped reporting, and once a day deletes expired history.
    /// </summary>
    internal void Sweep()
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var (key, tracked) in _sessions)
            {
                if (now - tracked.LastReportUtc >= StaleAfter && _sessions.TryRemove(key, out _))
                {
                    lock (tracked)
                    {
                        // Only the time up to the last report was watched.
                        Save(tracked, tracked.LastReportUtc, completed: false);
                    }
                }
            }

            if (now - _lastCleanup >= TimeSpan.FromDays(1))
            {
                _lastCleanup = now;
                var deleted = _history.DeleteExpired();
                if (deleted > 0)
                {
                    _logger.LogInformation("Deleted {Count} playback history rows older than two years", deleted);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sweep the playback history");
        }
    }

    private static string Key(PlaybackProgressEventArgs e, Guid userId)
    {
        var playSession = string.IsNullOrEmpty(e.PlaySessionId) ? e.Session?.Id ?? e.DeviceId : e.PlaySessionId;
        return string.Create(CultureInfo.InvariantCulture, $"{userId:N}:{e.Item.Id:N}:{playSession}");
    }

    private static void Advance(Tracked tracked, DateTime now)
    {
        var elapsed = now - tracked.LastReportUtc;
        if (!tracked.IsPaused && elapsed > TimeSpan.Zero)
        {
            tracked.Record.WatchedSeconds += (long)(elapsed < _maxGap ? elapsed : _maxGap).TotalSeconds;
        }

        tracked.LastReportUtc = now;
    }

    private PlaybackSessionRecord CreateRecord(PlaybackProgressEventArgs e, Guid userId, DateTime now)
    {
        var item = e.Item;
        var episode = item as Episode;
        return new PlaybackSessionRecord
        {
            UserId = userId,
            ItemId = item.Id,
            SeriesId = episode is null || episode.SeriesId.IsEmpty() ? null : episode.SeriesId,
            ItemType = item is Movie ? "Movie" : "Episode",
            Library = GetLibrary(item),
            ItemName = item.Name,
            SeriesName = episode?.SeriesName,
            SeasonNumber = episode?.ParentIndexNumber,
            EpisodeNumber = episode?.IndexNumber,
            StartUtc = now,
            EndUtc = now,
            PositionTicks = e.PlaybackPositionTicks,
            RuntimeTicks = item.RunTimeTicks,
            Client = e.ClientName,
            Device = e.DeviceName
        };
    }

    private string? GetLibrary(BaseItem item)
    {
        try
        {
            return _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't find the library of {Item}", item.Name);
            return null;
        }
    }

    private void Save(Tracked tracked, DateTime endUtc, bool completed)
    {
        try
        {
            tracked.Record.EndUtc = endUtc;
            tracked.Record.Completed = completed;
            tracked.Id = _history.SaveSession(tracked.Record, tracked.Id);
            tracked.LastSavedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save the playback history of {Item}", tracked.Record.ItemName);
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => OnStart(e);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => OnProgress(e);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => OnStopped(e);

    private sealed class Tracked(PlaybackSessionRecord record)
    {
        public PlaybackSessionRecord Record { get; } = record;

        public long? Id { get; set; }

        public DateTime LastReportUtc { get; set; }

        public DateTime LastSavedUtc { get; set; }

        public bool IsPaused { get; set; }
    }
}
