using System.Collections.Generic;
using MediaBrowser.Model.PirateFin;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A user's requests and how many more they may make.
/// </summary>
public class MyRequestsDto
{
    /// <summary>
    /// Gets or sets the open requests and the ones closed in the last 30 days, newest first.
    /// </summary>
    public IReadOnlyList<DownloadRequest> Requests { get; set; } = [];

    /// <summary>
    /// Gets or sets the most open requests at a time; -1 for no limit.
    /// </summary>
    public int Limit { get; set; }

    /// <summary>
    /// Gets or sets the open requests.
    /// </summary>
    public int Open { get; set; }

    /// <summary>
    /// Gets or sets the requests the user can still make; -1 when there's no limit.
    /// </summary>
    public int Remaining { get; set; }
}
