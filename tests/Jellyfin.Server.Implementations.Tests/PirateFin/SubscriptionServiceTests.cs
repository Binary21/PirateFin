using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Subscriptions;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The author search window: authors only for 10 minutes from the first attempt, then fallback sources for the user,
/// with authors still retried in the background.
/// </summary>
public class SubscriptionServiceTests
{
    private static readonly DateTime _start = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IEpisodeSubscriptionRepository> _repository = new();
    private readonly Mock<ITorrentSearchService> _torrentSearch = new();
    private readonly Mock<IDownloadManager> _downloadManager = new();
    private readonly Mock<IEpisodeVersions> _versions = new();
    private readonly ManualTimeProvider _time = new(_start);
    private readonly SubscriptionOptions _options = new();
    private readonly SubscriptionService _service;

    public SubscriptionServiceTests()
    {
        var monitor = Mock.Of<IOptionsMonitor<SubscriptionOptions>>(m => m.CurrentValue == _options);
        _service = new SubscriptionService(
            _repository.Object,
            _torrentSearch.Object,
            _downloadManager.Object,
            Mock.Of<ISeasonSubscriptions>(),
            Mock.Of<IReleaseSearchPauses>(),
            monitor,
            _versions.Object,
            _time,
            NullLogger<SubscriptionService>.Instance);
    }

    [Fact]
    public async Task Anime_UnfollowedSeasonWithPrioritizeDub_TriesTheDubFirstThenTheSub()
    {
        var episode = AiredEpisode();
        episode.LibraryType = "Anime";
        Due(episode);
        _versions.Setup(v => v.GetDefaultLanguage()).Returns(ReleaseLanguage.Dub);
        var sub = Release("[SubsPlease] Show - 05") with { Language = ReleaseLanguage.Sub };
        _torrentSearch.Setup(t => t.FindAuthorReleaseAsync(episode, ReleaseLanguage.Sub, It.IsAny<CancellationToken>())).ReturnsAsync(sub);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _torrentSearch.Verify(t => t.FindAuthorReleaseAsync(episode, ReleaseLanguage.Dub, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(sub.MagnetUrl, episode.DownloadUrl);
        Assert.Equal(ReleaseLanguage.Sub, episode.ReleaseLanguage);
    }

    [Fact]
    public async Task Anime_WithoutPrioritizeDub_SearchesOnlyTheSubs()
    {
        var episode = AiredEpisode();
        episode.LibraryType = "Anime";
        Due(episode);
        _versions.Setup(v => v.GetDefaultLanguage()).Returns(ReleaseLanguage.Sub);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _torrentSearch.Verify(t => t.FindAuthorReleaseAsync(episode, ReleaseLanguage.Dub, It.IsAny<CancellationToken>()), Times.Never);
        _torrentSearch.Verify(t => t.FindAuthorReleaseAsync(episode, ReleaseLanguage.Sub, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Research_ForgetsReleases_AndReplacesDownloadedOnlyWhenAsked()
    {
        var found = AiredEpisode();
        found.DownloadUrl = "magnet:old";
        found.IsDownloadable = true;
        found.SourceSearchStartedUtc = _start;
        var downloaded = AiredEpisode();
        downloaded.IsDownloaded = true;
        downloaded.IsInternet = false;
        downloaded.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".mkv");
        var downloading = AiredEpisode();
        downloading.DownloadStartedUtc = _start;
        _downloadManager.Setup(d => d.GetPendingDownloads()).Returns([]);

        var kept = await _service.ResearchAsync([found, downloaded, downloading], false, CancellationToken.None);
        Assert.Equal((1, 0, 1), kept);
        Assert.Empty(found.DownloadUrl);
        Assert.Null(found.SourceSearchStartedUtc);
        Assert.True(found.Scheduled);
        Assert.True(downloaded.IsDownloaded);

        var replaced = await _service.ResearchAsync([downloaded], true, CancellationToken.None);
        Assert.Equal((1, 1, 0), replaced);
        Assert.False(downloaded.IsDownloaded);
        Assert.True(downloaded.IsVirtualItem);
        Assert.True(downloaded.IsInternet);
    }

    private static TorrentCandidate Release(string title, int seeders = 10)
        => new() { Title = title, MagnetUrl = "magnet:?xt=urn:btih:" + title.GetHashCode(StringComparison.Ordinal), Seeders = seeders };

    private static Episode AiredEpisode() => new()
    {
        Id = Guid.NewGuid(),
        IsInternet = true,
        Scheduled = true,
        IsDownloaded = false,
        FirstAiring = _start.AddHours(-1)
    };

    private void Due(params Episode[] episodes)
        => _repository.Setup(r => r.GetDueEpisodes(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<Func<Episode, bool>>())).Returns(episodes);

    [Fact]
    public async Task AuthorRelease_SubscribedSeason_IsStoredAndQueued()
    {
        var episode = AiredEpisode();
        Due(episode);
        var release = Release("[SubsPlease] Show - 05");
        _torrentSearch.Setup(t => t.FindAuthorReleaseAsync(episode, It.IsAny<CancellationToken>())).ReturnsAsync(release);
        _repository.Setup(r => r.IsSeasonSubscribed(episode)).Returns(true);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.Equal(release.MagnetUrl, episode.DownloadUrl);
        Assert.True(episode.IsDownloadable);
        Assert.Equal(ReleaseStatus.Available, ReleaseStatusResolver.Resolve(episode, _start));
        _downloadManager.Verify(d => d.QueueDownload(episode), Times.Once);
    }

    [Fact]
    public async Task AuthorRelease_UnsubscribedSeason_IsStoredButNotQueued()
    {
        var episode = AiredEpisode();
        Due(episode);
        _torrentSearch.Setup(t => t.FindAuthorReleaseAsync(episode, It.IsAny<CancellationToken>())).ReturnsAsync(Release("x"));
        _repository.Setup(r => r.IsSeasonSubscribed(episode)).Returns(false);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.True(episode.IsDownloadable);
        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<Episode>()), Times.Never);
    }

    [Fact]
    public async Task NoAuthorRelease_InsideWindow_RetriesAuthorsWithoutFallback()
    {
        var episode = AiredEpisode();
        Due(episode);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.Equal(_start, episode.SourceSearchStartedUtc);
        Assert.Equal(_start + _options.AuthorRetryInterval, episode.NextSourceSearchUtc);
        Assert.Empty(episode.SourceCandidates);
        Assert.Equal(ReleaseStatus.Searching, ReleaseStatusResolver.Resolve(episode, _start));
        _torrentSearch.Verify(t => t.FindFallbackReleasesAsync(It.IsAny<Episode>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoAuthorRelease_LastRetry_LandsAtWindowEnd()
    {
        var episode = AiredEpisode();
        episode.SourceSearchStartedUtc = _start.AddMinutes(-9);
        Due(episode);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.Equal(_start.AddMinutes(1), episode.NextSourceSearchUtc);
    }

    [Fact]
    public async Task NoAuthorRelease_AfterWindow_OffersFallbackAndKeepsRetryingAuthors()
    {
        var episode = AiredEpisode();
        episode.SourceSearchStartedUtc = _start.AddMinutes(-10);
        Due(episode);
        var fallback = new List<TorrentCandidate> { Release("a", 50), Release("b", 20) };
        _torrentSearch.Setup(t => t.FindFallbackReleasesAsync(episode, 5, It.IsAny<CancellationToken>())).ReturnsAsync(fallback);
        _repository.Setup(r => r.IsSeasonSubscribed(episode)).Returns(true);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.Equal(fallback, episode.SourceCandidates);
        Assert.Equal(ReleaseStatus.ChooseSource, ReleaseStatusResolver.Resolve(episode, _start));
        Assert.Equal(_start + _options.BackgroundAuthorRetryInterval, episode.NextSourceSearchUtc);

        // Fallback sources are never downloaded without the user choosing one, even for subscribed seasons.
        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<Episode>()), Times.Never);
        _torrentSearch.Verify(t => t.FindAuthorReleaseAsync(episode, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task EpisodeAiredOverAWeekAgo_GetsOneFallbackSearch_ThenStops(int found)
    {
        var episode = AiredEpisode();
        episode.FirstAiring = _start.AddDays(-8);
        episode.SourceSearchStartedUtc = _start.AddMinutes(-10);
        Due(episode);
        var fallback = new List<TorrentCandidate>();
        for (var i = 0; i < found; i++)
        {
            fallback.Add(Release("r" + i));
        }

        _torrentSearch.Setup(t => t.FindFallbackReleasesAsync(episode, 5, It.IsAny<CancellationToken>())).ReturnsAsync(fallback);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.False(episode.Scheduled);
        Assert.Null(episode.NextSourceSearchUtc);
        Assert.Equal(
            found == 0 ? ReleaseStatus.NotFound : ReleaseStatus.ChooseSource,
            ReleaseStatusResolver.Resolve(episode, _start));
        _torrentSearch.Verify(t => t.FindFallbackReleasesAsync(episode, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AuthorReleaseAfterFallback_SubscribedSeason_DownloadsAndClearsFallback()
    {
        var episode = AiredEpisode();
        episode.SourceSearchStartedUtc = _start.AddHours(-2);
        episode.SourceCandidates = [Release("fallback")];
        Due(episode);
        _torrentSearch.Setup(t => t.FindAuthorReleaseAsync(episode, It.IsAny<CancellationToken>())).ReturnsAsync(Release("author"));
        _repository.Setup(r => r.IsSeasonSubscribed(episode)).Returns(true);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.Empty(episode.SourceCandidates);
        _downloadManager.Verify(d => d.QueueDownload(episode), Times.Once);
    }

    [Fact]
    public async Task FreshFallback_IsNotSearchedAgain()
    {
        var episode = AiredEpisode();
        episode.SourceSearchStartedUtc = _start.AddHours(-1);
        episode.SourceCandidates = [Release("fallback")];
        episode.SourceCandidatesUpdatedUtc = _start.AddMinutes(-30);
        Due(episode);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _torrentSearch.Verify(t => t.FindFallbackReleasesAsync(It.IsAny<Episode>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GivesUp_AfterGiveUpPeriod()
    {
        var episode = AiredEpisode();
        episode.SourceSearchStartedUtc = _start - _options.GiveUpAfter;
        Due(episode);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.False(episode.Scheduled);
        Assert.Equal(ReleaseStatus.NotFound, ReleaseStatusResolver.Resolve(episode, _start));
    }

    [Fact]
    public async Task OneEpisodeFailing_DoesNotStopOthers()
    {
        var failing = AiredEpisode();
        var working = AiredEpisode();
        Due(failing, working);
        _torrentSearch.Setup(t => t.FindAuthorReleaseAsync(failing, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("jackett down"));
        _torrentSearch.Setup(t => t.FindAuthorReleaseAsync(working, It.IsAny<CancellationToken>())).ReturnsAsync(Release("x"));

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        Assert.True(working.IsDownloadable);
    }

    [Fact]
    public async Task SelectSource_UsesCandidateAndQueuesDownload()
    {
        var episode = AiredEpisode();
        var chosen = Release("b", 20);
        episode.SourceCandidates = [Release("a", 50), chosen];

        var selected = await _service.SelectSourceAsync(episode, 1, CancellationToken.None);

        Assert.True(selected);
        Assert.Equal(chosen.MagnetUrl, episode.DownloadUrl);
        _downloadManager.Verify(d => d.QueueDownload(episode), Times.Once);
    }

    [Fact]
    public async Task SelectSource_InvalidIndex_ReturnsFalse()
    {
        var episode = AiredEpisode();
        episode.SourceCandidates = [Release("a")];

        Assert.False(await _service.SelectSourceAsync(episode, 3, CancellationToken.None));
        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<Episode>()), Times.Never);
    }

    private sealed class ManualTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
