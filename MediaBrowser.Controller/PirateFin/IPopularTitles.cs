using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// The Popular rows: each library's most popular recent titles, refreshed daily.
/// </summary>
public interface IPopularTitles
{
    /// <summary>
    /// Gets a library's popular titles, most popular first (up to 50).
    /// </summary>
    /// <param name="library">The library: Shows, Anime or Movies.</param>
    /// <returns>The series / movie ids.</returns>
    IReadOnlyList<Guid> GetTitles(string library);
}
