namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// How a series or season is searched for on Jackett, and what an admin overrode. Only the search uses these; the
/// displayed name and season number never change.
/// </summary>
public class SearchOverrideDto
{
    /// <summary>
    /// Gets or sets the search title in use: the override, or the first default title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the first title searched without an override.
    /// </summary>
    public string DefaultTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the series has a title override.
    /// </summary>
    public bool IsTitleOverridden { get; set; }

    /// <summary>
    /// Gets or sets the season number in use (seasons only).
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the season's own number (seasons only).
    /// </summary>
    public int? DefaultSeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the season has a season number override.
    /// </summary>
    public bool IsSeasonNumberOverridden { get; set; }
}
