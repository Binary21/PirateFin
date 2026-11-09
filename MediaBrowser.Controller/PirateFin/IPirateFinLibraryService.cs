using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Fills the PirateFin libraries from the weekly schedule, and resets them.
/// </summary>
public interface IPirateFinLibraryService
{
    /// <summary>
    /// Fetches the newest items for a library (AniList / Trakt / YTS) and creates placeholders for them.
    /// </summary>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RefreshAsync(string library, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every placeholder of a library that is not downloaded or downloading. Seasons and series are removed
    /// too once nothing is left in them.
    /// </summary>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <returns>What was removed.</returns>
    PlaceholderRemovalResult RemovePlaceholders(string library);

    /// <summary>
    /// Removes the library's placeholders and starts a fresh <see cref="RefreshAsync"/> in the background.
    /// </summary>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <returns>What was removed, or <c>null</c> when a reset of this library is already running.</returns>
    PlaceholderRemovalResult? StartReset(string library);
}
