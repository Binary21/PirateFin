using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Storage access for the subscription / source search loop.
/// </summary>
public interface IEpisodeSubscriptionRepository
{
    /// <summary>
    /// Gets aired placeholder episodes that have no download source yet and are due for a search.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <param name="limit">The maximum number of episodes.</param>
    /// <param name="skip">Leaves out episodes that aren't to be searched now (paused), before the limit applies.</param>
    /// <returns>The episodes, least recently searched first.</returns>
    IReadOnlyList<Episode> GetDueEpisodes(DateTime utcNow, int limit, Func<Episode, bool> skip);

    /// <summary>
    /// Gets a value indicating whether the episode's season is subscribed.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <returns>Whether the season is subscribed.</returns>
    bool IsSeasonSubscribed(Episode episode);

    /// <summary>
    /// Gets the seasons anyone is subscribed to.
    /// </summary>
    /// <returns>The seasons.</returns>
    IReadOnlyList<Season> GetSubscribedSeasons();

    /// <summary>
    /// Persists changes to the episode.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(Episode episode, CancellationToken cancellationToken);
}
