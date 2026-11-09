namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A new search override: the title for a series, the season number for a season.
/// </summary>
public class SaveSearchOverrideDto
{
    /// <summary>
    /// Gets or sets the search title (series).
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the search season number (season).
    /// </summary>
    public int? SeasonNumber { get; set; }
}
