using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Class RefreshNewestShowsTask.
/// </summary>
public class RefreshWeeklyScheduleTask : IScheduledTask
{
    private readonly ILogger<RefreshWeeklyScheduleTask> _logger;
    private readonly IInternetScheduleFetcher _internetScheduleFetcher;
    private readonly IEpisodeGenerator _episodeGenerator;
    private readonly ILibraryManager _libraryManager;
    private readonly IMovieGenerator _movieGenerator;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshWeeklyScheduleTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="internetScheduleFetcher">Instance of the <see cref="IInternetScheduleFetcher"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="episodeGenerator">Insance of the <see cref="IEpisodeGenerator"/> interface.</param>
    /// <param name="movieGenerator">Instance of the <see cref="IMovieGenerator"/> interface.</param>
    public RefreshWeeklyScheduleTask(ILogger<RefreshWeeklyScheduleTask> logger, IInternetScheduleFetcher internetScheduleFetcher, ILibraryManager libraryManager, IEpisodeGenerator episodeGenerator, IMovieGenerator movieGenerator) // , IProviderManager providerManager, MediaBrowser.Model.IO.IFileSystem fileSystem) // , INewestMoviesCache cache)
    {
        _logger = logger;
        _internetScheduleFetcher = internetScheduleFetcher;
        _libraryManager = libraryManager;
        _episodeGenerator = episodeGenerator;
        _movieGenerator = movieGenerator;
    }

     /// <inheritdoc />
    public string Name => "Refresh Newest Shows";

    /// <inheritdoc />
    public string Key => "RefreshNewestShows";

    /// <inheritdoc />
    public string Description => "Fetches the newest shows and creates placeholders in the library.";

    /// <inheritdoc />
    public string Category => "Library";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[RefreshWeeklyScheduleTask] Starting refresh...");

            List<ScheduleEpisode> animeResult = new List<ScheduleEpisode>();
            List<ScheduleEpisode> tvResult = new List<ScheduleEpisode>();
            List<ScheduleMovie> movieResult = new List<ScheduleMovie>();
            try
            {
                animeResult = await _internetScheduleFetcher.GetAnimeWeeklySchedule().ConfigureAwait(false);
            }
            catch (Exception)
            {
                _logger.LogError("[RefreshNewestAnimeTask] An error occured while attempting to fetch AniList Weekly episodes, AniList may be down.");
            }

            try
            {
                tvResult = await _internetScheduleFetcher.GetTVShowWeeklySchedule().ConfigureAwait(false);
            }
            catch (Exception)
            {
                _logger.LogError("[RefreshNewestShowsTask] An error occured while attempting to fetch TraktTv Weekly episodes, TraktTv may be down.");
            }

            try
            {
                movieResult = await _internetScheduleFetcher.GetMovieLatestSchedule().ConfigureAwait(false);
            }
            catch (Exception)
            {
                _logger.LogError("[RefreshNewestMoviesTask] An error occured while attempting to fetch YTS newest episodes, YTS endpoint may have changed.");
            }

            DateTime today = DateTime.Today;
            DateTime startOfWeek = today.AddDays(-(int)today.DayOfWeek);
            _logger.LogInformation("[RefreshNewestShowsTask] Fetched Anime Episodes for the week of {Week}.", startOfWeek);

            // TODO: replace with configurable name
            await _episodeGenerator.GenerateEpisode(animeResult, "Anime", startOfWeek).ConfigureAwait(false);

            await _episodeGenerator.GenerateEpisode(tvResult, "Shows", startOfWeek).ConfigureAwait(false);

            await _movieGenerator.GenerateMovie(movieResult, "Movies").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RefreshNewestShowsTask] An error occured while attempting to refresh this weeks schedule.");
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(1).Ticks
        };
    }
}
