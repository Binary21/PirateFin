using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A series whose files are on more than one drive.
/// </summary>
public class SplitSeries
{
    /// <summary>
    /// Gets or sets the series name (its folder name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the series' library folder.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many bytes of the series are on each drive (e.g. "C:\").
    /// </summary>
    public Dictionary<string, long> BytesPerDrive { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether no single drive can hold the whole series and keep the minimum free space.
    /// </summary>
    public bool CannotFit { get; set; }

    /// <summary>
    /// Gets or sets the outcome of the last attempt to move it back together, if any.
    /// </summary>
    public string? LastAttempt { get; set; }
}
