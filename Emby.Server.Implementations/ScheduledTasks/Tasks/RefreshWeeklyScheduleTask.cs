using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Creates placeholders for this week's anime / TV episodes and this year's movies.
/// </summary>
public class RefreshWeeklyScheduleTask : IScheduledTask
{
    private static readonly string[] _libraries = [PirateFinLibraries.Anime, PirateFinLibraries.Shows, PirateFinLibraries.Movies];

    private readonly ILogger<RefreshWeeklyScheduleTask> _logger;
    private readonly IPirateFinLibraryService _libraryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshWeeklyScheduleTask"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryService">The PirateFin library service.</param>
    public RefreshWeeklyScheduleTask(ILogger<RefreshWeeklyScheduleTask> logger, IPirateFinLibraryService libraryService)
    {
        _logger = logger;
        _libraryService = libraryService;
    }

    /// <inheritdoc />
    public string Name => "Refresh Newest Shows";

    /// <inheritdoc />
    public string Key => "RefreshNewestShows";

    /// <inheritdoc />
    public string Description => "Fetches this week's anime and TV episodes and this year's movies, and creates placeholders in the library.";

    /// <inheritdoc />
    public string Category => "PirateFin";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _libraries.Length; i++)
        {
            // One provider being down (or one library failing to generate) must not stop the others.
            try
            {
                await _libraryService.RefreshAsync(_libraries[i], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Refreshing the newest {Library} failed", _libraries[i]);
            }

            progress.Report(100.0 * (i + 1) / _libraries.Length);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks };
    }
}
