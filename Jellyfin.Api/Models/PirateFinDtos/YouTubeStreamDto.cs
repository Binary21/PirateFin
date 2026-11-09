namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A started stream.
/// </summary>
public class YouTubeStreamDto
{
    /// <summary>
    /// Gets or sets the HLS playlist, relative to the server address.
    /// </summary>
    public string Url { get; set; } = string.Empty;
}
