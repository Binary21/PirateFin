using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Searches Trakt / AniList / YTS and adds a chosen result to the library.
/// </summary>
public interface IInternetSearchService
{
    /// <summary>
    /// Searches for series. Does not touch the library.
    /// </summary>
    /// <param name="library">The library (<see cref="PirateFinLibraries.Shows"/>, <see cref="PirateFinLibraries.Anime"/> or <see cref="PirateFinLibraries.Movies"/>).</param>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The results.</returns>
    Task<IReadOnlyList<InternetSearchResult>> SearchAsync(string library, string query, CancellationToken cancellationToken);

    /// <summary>
    /// Creates (or updates) the library series or movie for a search result.
    /// </summary>
    /// <param name="library">The library the result came from.</param>
    /// <param name="key">The <see cref="InternetSearchResult.Key"/>.</param>
    /// <param name="query">The search text the result came from, used to rebuild expired results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series / movie id, or <c>null</c> when the result could not be resolved.</returns>
    Task<Guid?> OpenAsync(string library, string key, string query, CancellationToken cancellationToken);

    /// <summary>
    /// Searches Trakt / AniList for a library series by its title and adds the seasons and episodes it is missing.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many seasons and episodes were added; <c>null</c> when the series wasn't found.</returns>
    Task<(int Seasons, int Episodes)?> RefreshSeriesAsync(Entities.TV.Series series, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="RefreshSeriesAsync"/>, but the given season is added (and its metadata fetched) first and the
    /// call returns once it's done; the series' other seasons are added afterwards in the background, oldest first.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="seasonNumber">The season to add first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many seasons and episodes were added to that season; <c>null</c> when the series wasn't found.</returns>
    Task<(int Seasons, int Episodes)?> RefreshSeasonFirstAsync(Entities.TV.Series series, int seasonNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a Trakt show to the Shows library (or finds it there), with its seasons and episodes.
    /// </summary>
    /// <param name="show">The show.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series id; <c>null</c> when it couldn't be added.</returns>
    Task<Guid?> AddShowAsync(Models.SearchedSeries show, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the anime series an AniList entry (a season) belongs to, to the Anime library (or finds it there).
    /// </summary>
    /// <param name="aniListId">The entry's AniList id.</param>
    /// <param name="title">Its preferred title.</param>
    /// <param name="romajiTitle">Its romaji title.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series id; <c>null</c> when it couldn't be found or added.</returns>
    Task<Guid?> AddAnimeAsync(string aniListId, string title, string romajiTitle, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a movie YTS has a download for to the Movies library (or finds it there).
    /// </summary>
    /// <param name="imdbId">The movie's IMDb id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movie id; <c>null</c> when YTS doesn't have it (or it doesn't pass the movie filters).</returns>
    Task<Guid?> AddMovieAsync(string imdbId, CancellationToken cancellationToken);
}
