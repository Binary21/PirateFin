using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Server.Implementations.PirateFin.Insights;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The playback log: sessions count only unpaused time, searches typed letter by letter are one search, and history
/// older than two years is deleted.
/// </summary>
public sealed class PlaybackHistoryTests : IDisposable
{
    private static readonly DateTime _start = new(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "piratefin-playback-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly SettableTimeProvider _time = new(_start);
    private readonly PlaybackHistoryStore _store;
    private readonly PlaybackTracker _tracker;
    private readonly User _user = new("viewer", "default", "default");
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Name = "A Movie", RunTimeTicks = TimeSpan.FromHours(2).Ticks };

    public PlaybackHistoryTests()
    {
        _store = new PlaybackHistoryStore(_dbPath, _time);
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetCollectionFolders(_movie)).Returns([]);
        _tracker = new PlaybackTracker(Mock.Of<MediaBrowser.Controller.Session.ISessionManager>(), libraryManager.Object, _store, _time, NullLogger<PlaybackTracker>.Instance);
    }

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" }.Where(File.Exists))
        {
            File.Delete(file);
        }

        _tracker.Dispose();
    }

    private PlaybackProgressEventArgs Report(bool paused = false, long position = 0) => new()
    {
        Item = _movie,
        Users = new List<User> { _user },
        IsPaused = paused,
        PlaybackPositionTicks = position,
        PlaySessionId = "play-1",
        ClientName = "Jellyfin Web",
        DeviceName = "Chrome"
    };

    private PlaybackSessionRecord Single() => Assert.Single(_store.GetSessions(_user.Id, _start.AddDays(-1), _start.AddDays(1)));

    [Fact]
    public void Tracker_CountsOnlyUnpausedTime()
    {
        _tracker.OnStart(Report());
        _time.Advance(TimeSpan.FromSeconds(10));
        _tracker.OnProgress(Report());
        _time.Advance(TimeSpan.FromSeconds(10));
        _tracker.OnProgress(Report(paused: true));

        // Paused for five minutes, then playing again.
        _time.Advance(TimeSpan.FromMinutes(5));
        _tracker.OnProgress(Report());
        _time.Advance(TimeSpan.FromSeconds(10));
        _tracker.OnStopped(new PlaybackStopEventArgs { Item = _movie, Users = new List<User> { _user }, PlaySessionId = "play-1", PlaybackPositionTicks = 42, PlayedToCompletion = true });

        var session = Single();
        Assert.Equal(30, session.WatchedSeconds);
        Assert.True(session.Completed);
        Assert.Equal(42, session.PositionTicks);
        Assert.Equal("Movie", session.ItemType);
        Assert.Equal("Chrome", session.Device);
        Assert.Equal(_start.AddSeconds(330), session.EndUtc);
    }

    [Fact]
    public void Tracker_CapsGapsBetweenReports()
    {
        _tracker.OnStart(Report());
        _time.Advance(TimeSpan.FromMinutes(5));
        _tracker.OnProgress(Report());
        _tracker.OnStopped(new PlaybackStopEventArgs { Item = _movie, Users = new List<User> { _user }, PlaySessionId = "play-1" });

        Assert.Equal(60, Single().WatchedSeconds);
    }

    [Fact]
    public void Tracker_ClosesSessionsThatStoppedReporting()
    {
        _tracker.OnStart(Report());
        _time.Advance(TimeSpan.FromSeconds(30));
        _tracker.OnProgress(Report());
        _time.Advance(PlaybackTracker.StaleAfter);
        _tracker.Sweep();

        var session = Single();
        Assert.Equal(30, session.WatchedSeconds);
        Assert.Equal(_start.AddSeconds(30), session.EndUtc);
        Assert.False(session.Completed);

        // A later report starts a new session rather than continuing the closed one.
        _tracker.OnProgress(Report());
        Assert.Equal(2, _store.GetSessions(_user.Id, _start.AddDays(-1), _start.AddDays(1)).Count);
    }

    [Fact]
    public void RecordSearch_WhileTyping_KeepsOneSearch()
    {
        var userId = Guid.NewGuid();
        foreach (var query in new[] { "na", "nar", "naru", "naruto" })
        {
            _store.RecordSearch(userId, "Anime", query, 0);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        // Another library, and the same library much later, are new searches.
        _store.RecordSearch(userId, "Shows", "naruto", 2);
        _time.Advance(TimeSpan.FromMinutes(5));
        _store.RecordSearch(userId, "Shows", "naruto shippuden", 0);

        var searches = _store.GetSearches(userId, _start.AddDays(-1), _start.AddDays(1));
        Assert.Equal(["naruto shippuden", "naruto", "naruto"], searches.Select(s => s.Query));
        Assert.Equal(["Shows", "Shows", "Anime"], searches.Select(s => s.Library));
    }

    [Fact]
    public void DeleteExpired_RemovesHistoryOlderThanTwoYears()
    {
        var old = new PlaybackSessionRecord { UserId = _user.Id, ItemId = _movie.Id, ItemType = "Movie", StartUtc = _start.AddDays(-731), EndUtc = _start.AddDays(-731) };
        var recent = new PlaybackSessionRecord { UserId = _user.Id, ItemId = _movie.Id, ItemType = "Movie", StartUtc = _start.AddDays(-700), EndUtc = _start.AddDays(-700) };
        _store.SaveSession(old, null);
        _store.SaveSession(recent, null);

        _store.DeleteExpired();

        var left = Assert.Single(_store.GetSessions(_user.Id, _start.AddYears(-3), _start));
        Assert.Equal(recent.StartUtc, left.StartUtc);
    }

    private sealed class SettableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;

        public override DateTimeOffset GetUtcNow() => new(_utcNow);
    }
}
