using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Where filling in the Shows / Anime libraries stands: the nightly run over recent and airing series, or a full run
/// over every series started from the dashboard.
/// </summary>
public class PopulateStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a run is going on.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets which run: "Full" (every series) or "Nightly" (recent and airing series).
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Gets or sets how many series the run goes through.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets how many series are done.
    /// </summary>
    public int Done { get; set; }

    /// <summary>
    /// Gets or sets how many series couldn't be filled in (not found on Trakt / AniList, or an error).
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets the series being filled in now.
    /// </summary>
    public string? Current { get; set; }

    /// <summary>
    /// Gets or sets when the run started.
    /// </summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the last run finished.
    /// </summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>
    /// Gets or sets until when the release searches it scheduled (spaced out to spare Jackett) go on, if later than now.
    /// </summary>
    public DateTime? ReleaseSearchesUntilUtc { get; set; }
}
