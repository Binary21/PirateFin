using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.Subscription;

/// <summary>
/// Pipeline stage 3b: turning due placeholder episodes into a persisted download URL and,
/// when the season is subscribed, a queued download ("download mock episode" trigger).
/// </summary>
public class SubscriptionServiceTests
{
    private readonly Mock<IEpisodeSubscriptionRepository> _repository = new();
    private readonly Mock<IDownloadUrlService> _downloadUrlService = new();
    private readonly Mock<IDownloadManager> _downloadManager = new();
    private readonly SubscriptionService _service;

    public SubscriptionServiceTests()
    {
        _service = new SubscriptionService(
            _repository.Object,
            _downloadUrlService.Object,
            _downloadManager.Object,
            Options.Create(new SubscriptionOptions { PageSize = 10, MaxConcurrency = 1 }),
            NullLogger<SubscriptionService>.Instance);
    }

    private void SetUpSinglePageOfCandidates(params Episode[] episodes)
    {
        _repository
            .SetupSequence(r => r.GetCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(episodes)
            .ReturnsAsync(Array.Empty<Episode>());
    }

    [Fact]
    public async Task ProcessDueEpisodesAsync_NoCandidates_DoesNothing()
    {
        _repository
            .Setup(r => r.GetCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Episode>());

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _downloadUrlService.Verify(d => d.GetDownloadUrlAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDueEpisodesAsync_CandidateWithUrlAndSubscribedSeason_PersistsAndQueuesDownload()
    {
        var episode = new Episode { Id = Guid.NewGuid() };
        SetUpSinglePageOfCandidates(episode);
        var candidate = new EpisodeDownloadCandidate("Title", "magnet:?xt=urn:btih:ABC", 10);
        _downloadUrlService.Setup(d => d.GetDownloadUrlAsync(episode, It.IsAny<CancellationToken>())).ReturnsAsync(candidate);
        _repository.Setup(r => r.IsSeasonSubscribedAsync(episode, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _repository.Verify(r => r.PersistDownloadUrlAsync(episode, candidate, It.IsAny<CancellationToken>()), Times.Once);
        _downloadManager.Verify(d => d.QueueDownload(episode, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessDueEpisodesAsync_SeasonNotSubscribed_PersistsButDoesNotQueue()
    {
        var episode = new Episode { Id = Guid.NewGuid() };
        SetUpSinglePageOfCandidates(episode);
        var candidate = new EpisodeDownloadCandidate("Title", "magnet:?xt=urn:btih:ABC", 10);
        _downloadUrlService.Setup(d => d.GetDownloadUrlAsync(episode, It.IsAny<CancellationToken>())).ReturnsAsync(candidate);
        _repository.Setup(r => r.IsSeasonSubscribedAsync(episode, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _repository.Verify(r => r.PersistDownloadUrlAsync(episode, candidate, It.IsAny<CancellationToken>()), Times.Once);
        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDueEpisodesAsync_NoDownloadCandidateFound_DoesNotPersistOrQueue()
    {
        var episode = new Episode { Id = Guid.NewGuid() };
        SetUpSinglePageOfCandidates(episode);
        _downloadUrlService
            .Setup(d => d.GetDownloadUrlAsync(episode, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EpisodeDownloadCandidate?)null);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _repository.Verify(r => r.PersistDownloadUrlAsync(It.IsAny<Episode>(), It.IsAny<EpisodeDownloadCandidate>(), It.IsAny<CancellationToken>()), Times.Never);
        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDueEpisodesAsync_OneEpisodeThrows_OtherEpisodeStillProcessed()
    {
        var failingEpisode = new Episode { Id = Guid.NewGuid() };
        var succeedingEpisode = new Episode { Id = Guid.NewGuid() };
        SetUpSinglePageOfCandidates(failingEpisode, succeedingEpisode);

        _downloadUrlService
            .Setup(d => d.GetDownloadUrlAsync(failingEpisode, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("jackett down"));
        var candidate = new EpisodeDownloadCandidate("Title", "magnet:?xt=urn:btih:ABC", 10);
        _downloadUrlService.Setup(d => d.GetDownloadUrlAsync(succeedingEpisode, It.IsAny<CancellationToken>())).ReturnsAsync(candidate);
        _repository.Setup(r => r.IsSeasonSubscribedAsync(succeedingEpisode, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _service.ProcessDueEpisodesAsync(CancellationToken.None);

        _downloadManager.Verify(d => d.QueueDownload(succeedingEpisode, It.IsAny<CancellationToken>()), Times.Once);
        _downloadManager.Verify(d => d.QueueDownload(failingEpisode, It.IsAny<CancellationToken>()), Times.Never);
    }
}
