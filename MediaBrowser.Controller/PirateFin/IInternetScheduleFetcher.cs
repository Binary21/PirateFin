using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.PirateFin.Models;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Fetches what is airing / newly released this week from Trakt, AniList and YTS.
/// </summary>
public interface IInternetScheduleFetcher
{
    /// <summary>
    /// Gets the anime episodes airing this week.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episodes, with <see cref="ScheduleEpisode.FirstAiring"/> in UTC.</returns>
    Task<IReadOnlyList<ScheduleEpisode>> GetAnimeWeeklyScheduleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the TV episodes airing this week.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episodes, with <see cref="ScheduleEpisode.FirstAiring"/> in UTC.</returns>
    Task<IReadOnlyList<ScheduleEpisode>> GetTvWeeklyScheduleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets this year's newest movies.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movies.</returns>
    Task<IReadOnlyList<ScheduleMovie>> GetLatestMoviesAsync(CancellationToken cancellationToken);
}
