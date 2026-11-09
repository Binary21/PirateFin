namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// How far a video was watched.
/// </summary>
public class YouTubeProgressDto
{
    /// <summary>
    /// Gets or sets the position.
    /// </summary>
    public double PositionSeconds { get; set; }

    /// <summary>
    /// Gets or sets the video's length.
    /// </summary>
    public double DurationSeconds { get; set; }
}
