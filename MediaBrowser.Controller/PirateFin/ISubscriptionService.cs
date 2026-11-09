using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.PirateFin.Models;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Finds download sources for aired placeholder episodes and downloads subscribed ones.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// Runs one pass of the source search over all due episodes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ProcessDueEpisodesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Searches whole-season author releases for a freshly created series and stores them on its episodes.
    /// </summary>
    /// <param name="searchedSeries">The series as fetched from the provider.</param>
    /// <param name="series">The library series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ProcessSeasonReleasesAsync(SearchedSeries searchedSeries, Series series, CancellationToken cancellationToken);

    /// <summary>
    /// Uses one of the episode's fallback sources and starts downloading it.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="index">The index into <see cref="Episode.SourceCandidates"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> when the index is out of range.</returns>
    Task<bool> SelectSourceAsync(Episode episode, int index, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes or unsubscribes a user to a season. Subscribing queues every episode that already has a source; the
    /// season keeps downloading new episodes while anyone is subscribed.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="subscribed">Whether to subscribe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetSubscribedAsync(Season season, Guid userId, bool subscribed, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes or unsubscribes a user to a season in a language (anime: "Dub if available" or Sub), or changes the
    /// language of their subscription. Subscribing to anime searches the wanted languages straight away.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="subscribed">Whether to subscribe.</param>
    /// <param name="language">The language; <c>null</c> keeps the one they had (or "Prioritize Dub" decides).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetSubscribedAsync(Season season, Guid userId, bool subscribed, MediaBrowser.Model.PirateFin.ReleaseLanguage? language, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the language version searches (dubs, mostly) of the subscribed anime seasons that are due.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ProcessVersionSearchesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the releases found for episodes (after the admin changed how they are searched) so the background search
    /// looks for them again, and optionally turns downloaded episodes back into placeholders (deleting their files) so
    /// they are downloaded again. Episodes downloading right now are left alone.
    /// </summary>
    /// <param name="episodes">The episodes.</param>
    /// <param name="replaceDownloaded">Whether downloaded episodes are replaced too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many will be searched again, how many of those were downloaded, and how many were skipped while downloading.</returns>
    Task<(int Searching, int Replaced, int SkippedDownloading)> ResearchAsync(IEnumerable<Episode> episodes, bool replaceDownloaded, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the episodes being searched right now.
    /// </summary>
    /// <returns>Their ids.</returns>
    IReadOnlyCollection<Guid> GetSearchingNow();

    /// <summary>
    /// Gets what the searches of the last 24 hours (since the server started, if later) found.
    /// </summary>
    /// <returns>How many found a preferred release, only fallback releases, or nothing, and since when.</returns>
    (int Preferred, int FallbackOnly, int Nothing, DateTime SinceUtc) GetRecentResults();
}
