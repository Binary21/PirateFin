namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// What refreshing a series from Trakt / AniList added.
/// </summary>
public class RefreshSeriesResultDto
{
    /// <summary>
    /// Gets or sets the number of seasons added.
    /// </summary>
    public int SeasonsAdded { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes added.
    /// </summary>
    public int EpisodesAdded { get; set; }
}
