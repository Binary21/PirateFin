using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Fills in the Shows / Anime series: their missing seasons and episodes (Trakt / AniList), metadata and images, and
/// release searches spaced out over time. Runs every night in the storage move window for series added in the last 7
/// days and series still airing; a full run over every series can be started from the dashboard.
/// </summary>
public interface ILibraryPopulator
{
    /// <summary>
    /// Gets where the current (or last) run stands.
    /// </summary>
    /// <returns>The status.</returns>
    PopulateStatus GetStatus();

    /// <summary>
    /// Starts a run over every series in the Shows and Anime libraries.
    /// </summary>
    /// <returns><c>false</c> when a run is already going on.</returns>
    bool StartFullRun();
}
