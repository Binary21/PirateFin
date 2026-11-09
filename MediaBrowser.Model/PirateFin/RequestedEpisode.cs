using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// An episode of a <see cref="DownloadRequest"/>.
/// </summary>
public class RequestedEpisode
{
    /// <summary>
    /// Gets or sets the episode id.
    /// </summary>
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the label, e.g. "S02E05: The Return".
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the magnet link the admin found, if any.
    /// </summary>
    public string? MagnetUrl { get; set; }
}
