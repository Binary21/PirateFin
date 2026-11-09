using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A user's request that an admin finds a release for episodes PirateFin couldn't find: one episode, or the missing
/// episodes of a season.
/// </summary>
public class DownloadRequest
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the id of the user who asked.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the name of the user who asked.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requested episode or season.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what was requested, e.g. "Season 2" or "S02E05: The Return".
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it was requested, in UTC.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public DownloadRequestStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when it was closed or cancelled, in UTC.
    /// </summary>
    public DateTime? ClosedUtc { get; set; }

    /// <summary>
    /// Gets or sets the admin's note for the user.
    /// </summary>
    public string? AdminNote { get; set; }

    /// <summary>
    /// Gets or sets the requested episodes.
    /// </summary>
    public List<RequestedEpisode> Episodes { get; set; } = [];
}
