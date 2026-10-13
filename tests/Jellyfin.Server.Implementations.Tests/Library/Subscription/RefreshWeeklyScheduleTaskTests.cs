using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.Subscription;

/// <summary>
/// Pipeline stage 1: fetching the weekly schedule and handing it to the episode generator.
/// </summary>
public class RefreshWeeklyScheduleTaskTests
{
    private readonly Mock<IInternetScheduleFetcher> _fetcher = new();
    private readonly Mock<IEpisodeGenerator> _episodeGenerator = new();
    private readonly Mock<IMovieGenerator> _movieGenerator = new();
    private readonly RefreshWeeklyScheduleTask _task;

    public RefreshWeeklyScheduleTaskTests()
    {
        _task = new RefreshWeeklyScheduleTask(
            NullLogger<RefreshWeeklyScheduleTask>.Instance,
            _fetcher.Object,
            Mock.Of<ILibraryManager>(),
            _episodeGenerator.Object,
            _movieGenerator.Object);
    }

    [Fact]
    public async Task ExecuteAsync_FetchesBothSchedules_GeneratesAnimeAndTvEpisodes()
    {
        var animeEpisodes = new List<ScheduleEpisode> { new() { Title = "Anime Show" } };
        var tvEpisodes = new List<ScheduleEpisode> { new() { Title = "TV Show" } };
        _fetcher.Setup(f => f.GetAnimeWeeklySchedule()).ReturnsAsync(animeEpisodes);
        _fetcher.Setup(f => f.GetTVShowWeeklySchedule()).ReturnsAsync(tvEpisodes);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _episodeGenerator.Verify(g => g.GenerateEpisode(animeEpisodes, "Anime", It.IsAny<DateTime>()), Times.Once);
        _episodeGenerator.Verify(g => g.GenerateEpisode(tvEpisodes, "Shows", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AnimeFetchThrows_StillGeneratesTvEpisodes()
    {
        var tvEpisodes = new List<ScheduleEpisode> { new() { Title = "TV Show" } };
        _fetcher.Setup(f => f.GetAnimeWeeklySchedule()).ThrowsAsync(new InvalidOperationException("AniList down"));
        _fetcher.Setup(f => f.GetTVShowWeeklySchedule()).ReturnsAsync(tvEpisodes);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _episodeGenerator.Verify(g => g.GenerateEpisode(It.Is<List<ScheduleEpisode>>(l => l.Count == 0), "Anime", It.IsAny<DateTime>()), Times.Once);
        _episodeGenerator.Verify(g => g.GenerateEpisode(tvEpisodes, "Shows", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TvFetchThrows_StillGeneratesAnimeEpisodes()
    {
        var animeEpisodes = new List<ScheduleEpisode> { new() { Title = "Anime Show" } };
        _fetcher.Setup(f => f.GetAnimeWeeklySchedule()).ReturnsAsync(animeEpisodes);
        _fetcher.Setup(f => f.GetTVShowWeeklySchedule()).ThrowsAsync(new InvalidOperationException("Trakt down"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _episodeGenerator.Verify(g => g.GenerateEpisode(animeEpisodes, "Anime", It.IsAny<DateTime>()), Times.Once);
        _episodeGenerator.Verify(g => g.GenerateEpisode(It.Is<List<ScheduleEpisode>>(l => l.Count == 0), "Shows", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_EpisodeGeneratorThrows_DoesNotPropagate()
    {
        _fetcher.Setup(f => f.GetAnimeWeeklySchedule()).ReturnsAsync(new List<ScheduleEpisode>());
        _fetcher.Setup(f => f.GetTVShowWeeklySchedule()).ReturnsAsync(new List<ScheduleEpisode>());
        _episodeGenerator
            .Setup(g => g.GenerateEpisode(It.IsAny<List<ScheduleEpisode>>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var exception = await Record.ExceptionAsync(() => _task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsStartupAndHourlyIntervalTriggers()
    {
        var triggers = _task.GetDefaultTriggers().ToList();

        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromHours(1).Ticks);
    }
}
