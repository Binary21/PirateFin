using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Providers;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

public class RefreshWeeklyScheduleTaskTests
{
    private readonly Mock<IPirateFinLibraryService> _libraryService = new();
    private readonly RefreshWeeklyScheduleTask _task;

    public RefreshWeeklyScheduleTaskTests()
    {
        _task = new RefreshWeeklyScheduleTask(NullLogger<RefreshWeeklyScheduleTask>.Instance, _libraryService.Object);
    }

    [Fact]
    public async Task ExecuteAsync_RefreshesEachLibrary()
    {
        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _libraryService.Verify(s => s.RefreshAsync("Anime", It.IsAny<CancellationToken>()), Times.Once);
        _libraryService.Verify(s => s.RefreshAsync("Shows", It.IsAny<CancellationToken>()), Times.Once);
        _libraryService.Verify(s => s.RefreshAsync("Movies", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_OneLibraryFailing_OthersStillRefresh()
    {
        _libraryService.Setup(s => s.RefreshAsync("Anime", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("AniList down"));

        var exception = await Record.ExceptionAsync(() => _task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        Assert.Null(exception);
        _libraryService.Verify(s => s.RefreshAsync("Shows", It.IsAny<CancellationToken>()), Times.Once);
        _libraryService.Verify(s => s.RefreshAsync("Movies", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsStartupAndHourly()
    {
        var triggers = _task.GetDefaultTriggers().ToList();

        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromHours(1).Ticks);
    }

    [Theory]
    [InlineData("2026-10-23T12:00:00Z", "2026-10-19T04:00:00Z")] // Friday during EDT (UTC-4): week starts Monday 00:00 EDT.
    [InlineData("2026-12-02T03:00:00Z", "2026-11-30T05:00:00Z")] // Tuesday 22:00 EST on Dec 1 (UTC-5).
    [InlineData("2026-10-19T03:59:00Z", "2026-10-12T04:00:00Z")] // Sunday 23:59 EDT still belongs to the previous week.
    public void GetWeekUtc_UsesEasternMondays(string now, string expectedStart)
    {
        var (start, end) = InternetScheduleFetcher.GetWeekUtc(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse(expectedStart, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime, start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
        Assert.Equal(TimeSpan.FromDays(7), end - start);
    }
}
