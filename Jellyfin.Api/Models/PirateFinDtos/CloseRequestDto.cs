using MediaBrowser.Model.PirateFin;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// How an admin closes a request.
/// </summary>
public class CloseRequestDto
{
    /// <summary>
    /// Gets or sets the outcome: Fulfilled or NotFound.
    /// </summary>
    public DownloadRequestStatus Status { get; set; }

    /// <summary>
    /// Gets or sets a note for the user.
    /// </summary>
    public string? Note { get; set; }
}
