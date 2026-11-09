using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// What the episode release searches are doing: the dashboard's "Release searches" panel.
/// </summary>
public class ReleaseSearchStatus
{
    /// <summary>
    /// Gets or sets when the counts were calculated, in UTC.
    /// </summary>
    public DateTime CalculatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the episodes being searched right now.
    /// </summary>
    public IReadOnlyList<ReleaseSearchEpisode> SearchingNow { get; set; } = [];

    /// <summary>
    /// Gets or sets how many episodes that aired in the last week are still being searched (retried until a release
    /// turns up, for up to two weeks).
    /// </summary>
    public int NewEpisodes { get; set; }

    /// <summary>
    /// Gets or sets how many older episodes are still waiting for their one search (the back catalogue pass).
    /// </summary>
    public int OldEpisodes { get; set; }

    /// <summary>
    /// Gets or sets how many searches are due now (waiting for a free slot).
    /// </summary>
    public int DueNow { get; set; }

    /// <summary>
    /// Gets or sets how many episodes wait for their air time before they're searched.
    /// </summary>
    public int WaitingToAir { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the old searches of every show are paused.
    /// </summary>
    public bool OldPaused { get; set; }

    /// <summary>
    /// Gets or sets when the last old episode is due to be searched, in UTC; <c>null</c> when none are left (or all are paused).
    /// </summary>
    public DateTime? OldPassEndsUtc { get; set; }

    /// <summary>
    /// Gets or sets the shows with episodes still to search, next search first.
    /// </summary>
    public IReadOnlyList<ReleaseSearchShow> Shows { get; set; } = [];

    /// <summary>
    /// Gets or sets how many searches in the last 24 hours found a release from a preferred group.
    /// </summary>
    public int FoundPreferred { get; set; }

    /// <summary>
    /// Gets or sets how many searches in the last 24 hours found only other (fallback) releases.
    /// </summary>
    public int FoundFallbackOnly { get; set; }

    /// <summary>
    /// Gets or sets how many searches in the last 24 hours found nothing.
    /// </summary>
    public int FoundNothing { get; set; }

    /// <summary>
    /// Gets or sets since when the results are counted, in UTC (24 hours ago, or the server start if later).
    /// </summary>
    public DateTime ResultsSinceUtc { get; set; }
}
