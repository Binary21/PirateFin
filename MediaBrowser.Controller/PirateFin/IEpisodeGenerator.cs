using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.PirateFin.Models;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Creates or updates placeholder episodes (and their series / seasons) from the weekly schedule.
/// </summary>
public interface IEpisodeGenerator
{
    /// <summary>
    /// Creates or updates placeholders for the given schedule.
    /// </summary>
    /// <param name="scheduleEpisodes">The schedule.</param>
    /// <param name="library">The library to create the items in.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task GenerateEpisodesAsync(IReadOnlyList<ScheduleEpisode> scheduleEpisodes, string library, CancellationToken cancellationToken);
}
