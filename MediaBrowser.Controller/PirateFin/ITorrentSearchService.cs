using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Finds torrents for episodes through Jackett.
/// </summary>
public interface ITorrentSearchService
{
    /// <summary>
    /// Searches each configured release author for the episode and returns the best seeded exact match.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release, or <c>null</c> when no author has released it.</returns>
    Task<TorrentCandidate?> FindAuthorReleaseAsync(Episode episode, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the title an episode search of the series tries first when no admin override is set: the series folder
    /// title (it keeps the Trakt / AniList title release groups use), else the series name.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <returns>The title, as it is sent to the indexer.</returns>
    string GetDefaultSearchTitle(Series series);

    /// <summary>
    /// Cleans text the way it is sent to the indexer (letters, digits and single spaces).
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The cleaned text.</returns>
    string CleanSearchText(string value);

    /// <summary>
    /// Searches the preferred groups of one anime language (TV episodes use the TV groups) and returns the best seeded
    /// exact match. Each group's language indicator is added to the query and required in the release name.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="language">The anime group list to search.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release, or <c>null</c> when no group of that list has released it.</returns>
    Task<TorrentCandidate?> FindAuthorReleaseAsync(Episode episode, ReleaseLanguage language, CancellationToken cancellationToken);

    /// <summary>
    /// Searches without an author and returns the highest seeded releases that are not for a different episode.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="count">The maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases, highest seeded first.</returns>
    Task<IReadOnlyList<TorrentCandidate>> FindFallbackReleasesAsync(Episode episode, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Searches author releases for whole seasons of a TV series at once.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Releases keyed by season number, then episode number.</returns>
    Task<IReadOnlyDictionary<int, IReadOnlyDictionary<int, TorrentCandidate>>> FindSeasonReleasesAsync(SearchedSeries series, CancellationToken cancellationToken);
}
