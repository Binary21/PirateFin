namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Seconds watched on one day.
/// </summary>
public sealed class DailyWatchTime
{
    /// <summary>
    /// Gets or sets the day, yyyy-MM-dd in the viewer's time zone.
    /// </summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the seconds watched.
    /// </summary>
    public long Seconds { get; set; }
}
