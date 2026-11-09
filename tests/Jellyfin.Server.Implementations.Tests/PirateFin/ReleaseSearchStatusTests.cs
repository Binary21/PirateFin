using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Subscriptions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The dashboard's release search counts: new (aired in the last week), old (back catalogue) and not aired yet, by show;
/// and pausing the old ones.
/// </summary>
public sealed class ReleaseSearchStatusTests : IDisposable
{
    private static readonly DateTime _now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private readonly Guid _breakingBad = Guid.NewGuid();
    private readonly Guid _airing = Guid.NewGuid();
    private readonly string _dataPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "piratefin-pauses-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<TimeProvider> _time = new();
    private DateTime _clock = _now;
    private readonly ReleaseSearchPauseStore _pauses;
    private readonly ReleaseSearchStatusService _service;

    public ReleaseSearchStatusTests()
    {
        _time.Setup(t => t.GetUtcNow()).Returns(() => new DateTimeOffset(_clock));
        _pauses = new ReleaseSearchPauseStore(
            Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath),
            NullLogger<ReleaseSearchPauseStore>.Instance);
        _service = new ReleaseSearchStatusService(
            _libraryManager.Object,
            Mock.Of<ISubscriptionService>(s => s.GetSearchingNow() == Array.Empty<Guid>()),
            _pauses,
            Mock.Of<IOptionsMonitor<SubscriptionOptions>>(o => o.CurrentValue == new SubscriptionOptions()),
            _time.Object,
            NullLogger<ReleaseSearchStatusService>.Instance);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_dataPath))
        {
            System.IO.Directory.Delete(_dataPath, true);
        }
    }

    private Episode Waiting(Guid seriesId, string name, DateTime? aired, DateTime? next = null, string downloadUrl = "")
        => new()
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            SeriesName = name,
            FirstAiring = aired,
            NextSourceSearchUtc = next,
            DownloadUrl = downloadUrl,
            IsInternet = true,
            Scheduled = true,
            IsDownloaded = false
        };

    private List<BaseItem> Library()
    {
        var episodes = new List<BaseItem>
        {
            Waiting(_breakingBad, "Breaking Bad", _now.AddYears(-12), _now.AddHours(5)),
            Waiting(_breakingBad, "Breaking Bad", _now.AddYears(-12), _now.AddHours(9)),
            Waiting(_airing, "Airing", _now.AddDays(-2)),
            Waiting(_airing, "Airing", _now.AddDays(3)),
            Waiting(_airing, "Airing", _now.AddDays(-1), downloadUrl: "magnet:?xt=urn:btih:x")
        };
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(episodes);
        return episodes;
    }

    [Fact]
    public void CountsByGroupAndShow()
    {
        Library();

        var status = _service.Calculate(_now);

        Assert.Equal(1, status.NewEpisodes);
        Assert.Equal(2, status.OldEpisodes);
        Assert.Equal(1, status.WaitingToAir);
        Assert.Equal(1, status.DueNow);
        Assert.Equal(_now.AddHours(9), status.OldPassEndsUtc);

        // Due now first.
        Assert.Collection(
            status.Shows,
            s => Assert.Equal(("Airing", 1, 0, (DateTime?)_now), (s.SeriesName, s.NewEpisodes, s.OldEpisodes, s.NextSearchUtc)),
            s => Assert.Equal(("Breaking Bad", 0, 2, (DateTime?)_now.AddHours(5)), (s.SeriesName, s.NewEpisodes, s.OldEpisodes, s.NextSearchUtc)));
    }

    [Fact]
    public void PausedShowIsCountedButHasNoNextSearch()
    {
        Library();
        _service.PauseOldSearches(_breakingBad);

        var status = _service.Calculate(_now);

        Assert.Equal(2, status.OldEpisodes);
        Assert.Null(status.OldPassEndsUtc);
        var show = Assert.Single(status.Shows, s => s.SeriesId.Equals(_breakingBad));
        Assert.True(show.Paused);
        Assert.Null(show.NextSearchUtc);
        Assert.Equal(_breakingBad, status.Shows[^1].SeriesId);
        Assert.True(_pauses.IsPaused(_breakingBad));
        Assert.False(_pauses.IsPaused(_airing));
    }

    [Fact]
    public async Task ResumeMovesOldSearchesOnByThePause()
    {
        var episodes = Library();
        _service.PauseOldSearches(null);
        Assert.True(_service.Calculate(_now).OldPaused);
        Assert.True(_pauses.IsPaused(_airing));

        _clock = _now.AddHours(2);
        await _service.ResumeOldSearchesAsync(null, CancellationToken.None);

        Assert.Null(_pauses.AllPausedUtc);
        Assert.Equal(_now.AddHours(7), ((Episode)episodes[0]).NextSourceSearchUtc);
        Assert.Equal(_now.AddHours(11), ((Episode)episodes[1]).NextSourceSearchUtc);

        // New episodes weren't paused.
        Assert.Null(((Episode)episodes[2]).NextSourceSearchUtc);
    }

    [Fact]
    public async Task StoppingAShowStopsOnlyItsOldSearchesAndClearsItsPause()
    {
        var episodes = Library();
        _service.PauseOldSearches(_breakingBad);

        var stopped = await _service.StopOldSearchesAsync(_breakingBad, CancellationToken.None);

        Assert.Equal(2, stopped);
        Assert.False(((Episode)episodes[0]).Scheduled);
        Assert.True(((Episode)episodes[2]).Scheduled);
        Assert.False(_pauses.IsPaused(_breakingBad));
    }

    [Fact]
    public void PausesSurviveARestart()
    {
        _service.PauseOldSearches(_breakingBad);

        var reloaded = new ReleaseSearchPauseStore(
            Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath),
            NullLogger<ReleaseSearchPauseStore>.Instance);

        Assert.Equal(_now, reloaded.GetShowPausedUtc(_breakingBad));
        Assert.Null(reloaded.AllPausedUtc);
    }
}
