using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// When and how much a user watches.
/// </summary>
public sealed class ActivitySummary
{
    /// <summary>
    /// Gets or sets when the user was last active on the server.
    /// </summary>
    public DateTime? LastActiveUtc { get; set; }

    /// <summary>
    /// Gets or sets when the user last watched something (from the playback log).
    /// </summary>
    public DateTime? LastWatchedUtc { get; set; }

    /// <summary>
    /// Gets or sets the number of days in a row, up to today (or yesterday), on which the user watched something.
    /// </summary>
    public int StreakDays { get; set; }

    /// <summary>
    /// Gets or sets the number of days in the last 30 on which the user watched something.
    /// </summary>
    public int ActiveDays30 { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched in the last 7 days.
    /// </summary>
    public long Seconds7 { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched in the last 30 days.
    /// </summary>
    public long Seconds30 { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched in the last 90 days.
    /// </summary>
    public long Seconds90 { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched per library in the last 90 days, most first.
    /// </summary>
    public IReadOnlyList<LibraryWatchTime> Libraries { get; set; } = [];
}
