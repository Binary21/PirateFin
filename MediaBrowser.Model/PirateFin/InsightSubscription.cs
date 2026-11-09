using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A season the user is subscribed to.
/// </summary>
public sealed class InsightSubscription
{
    /// <summary>
    /// Gets or sets the season id.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    public Guid SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season name.
    /// </summary>
    public string SeasonName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string? Library { get; set; }

    /// <summary>
    /// Gets or sets when the user subscribed.
    /// </summary>
    public DateTime SinceUtc { get; set; }
}
