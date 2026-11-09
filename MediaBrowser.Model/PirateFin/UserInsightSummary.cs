using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A user's line on the dashboard's Users list.
/// </summary>
public sealed class UserInsightSummary
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched in the last 7 days.
    /// </summary>
    public long Seconds7 { get; set; }

    /// <summary>
    /// Gets or sets the genre the user watched the most titles of, over all libraries.
    /// </summary>
    public string? TopGenre { get; set; }
}
