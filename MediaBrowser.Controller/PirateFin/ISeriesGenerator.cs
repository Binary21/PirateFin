using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.PirateFin.Models;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Creates or updates a full placeholder series from a search result.
/// </summary>
public interface ISeriesGenerator
{
    /// <summary>
    /// Creates (or updates) the series with all of its seasons and episodes. Metadata is fetched in the background.
    /// </summary>
    /// <param name="searchedSeries">The series.</param>
    /// <param name="library">The library to create the series in.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="into">An existing series (e.g. one the user added) to add the missing seasons / episodes to.</param>
    /// <param name="first">Whether the metadata of what's added is fetched ahead of everything else waiting.</param>
    /// <returns>The series, or <c>null</c> when the library is not configured.</returns>
    Task<Series?> GenerateSeriesAsync(SearchedSeries searchedSeries, string library, CancellationToken cancellationToken, Series? into = null, bool first = false);

    /// <summary>
    /// Gets the id of the series if it already exists in the library.
    /// </summary>
    /// <param name="title">The series title.</param>
    /// <param name="library">The library.</param>
    /// <returns>The id, or <c>null</c> when the series does not exist.</returns>
    Guid? FindExistingSeriesId(string title, string library);
}
