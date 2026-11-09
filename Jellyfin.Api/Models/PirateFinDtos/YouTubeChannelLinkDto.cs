namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The channel to link by.
/// </summary>
public class YouTubeChannelLinkDto
{
    /// <summary>
    /// Gets or sets the channel id, @handle or link.
    /// </summary>
    public string Channel { get; set; } = string.Empty;
}
