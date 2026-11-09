namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Where a PirateFin (internet sourced) item is in its release / download lifecycle.
/// </summary>
public enum ReleaseStatus
{
    /// <summary>
    /// Not a PirateFin item; a regular library item.
    /// </summary>
    None = 0,

    /// <summary>
    /// The item has not aired yet.
    /// </summary>
    Unaired = 1,

    /// <summary>
    /// The item has aired and the configured release authors are being searched.
    /// </summary>
    Searching = 2,

    /// <summary>
    /// No author release was found within the search window; the user can pick one of the fallback sources.
    /// </summary>
    ChooseSource = 3,

    /// <summary>
    /// A download source is known; downloading starts when the item is opened (or immediately when subscribed).
    /// </summary>
    Available = 4,

    /// <summary>
    /// The download has started.
    /// </summary>
    Downloading = 5,

    /// <summary>
    /// The download has finished.
    /// </summary>
    Downloaded = 6,

    /// <summary>
    /// Searching gave up without finding any source.
    /// </summary>
    NotFound = 7
}
