using System;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// A library video (episode or movie) PirateFin can download from a torrent.
/// </summary>
public interface IPirateFinDownloadable
{
    /// <summary>
    /// Gets or sets the magnet url of the chosen release.
    /// </summary>
    string DownloadUrl { get; set; }

    /// <summary>
    /// Gets or sets the seeders of the chosen release.
    /// </summary>
    int? Seeders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="DownloadUrl"/> is set.
    /// </summary>
    bool IsDownloadable { get; set; }

    /// <summary>
    /// Gets or sets when the download started, in UTC; <c>null</c> when not downloading.
    /// </summary>
    DateTime? DownloadStartedUtc { get; set; }
}
