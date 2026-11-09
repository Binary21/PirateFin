using System.Collections.Generic;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Moves series that were split across drives (when their drive filled up) back onto one drive, overnight.
/// </summary>
public interface IStorageRebalancer
{
    /// <summary>
    /// Gets the series currently split across drives.
    /// </summary>
    /// <returns>The series.</returns>
    IReadOnlyList<SplitSeries> GetSplitSeries();
}
