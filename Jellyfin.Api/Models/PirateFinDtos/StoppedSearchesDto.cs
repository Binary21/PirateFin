namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The result of stopping the older episodes' release searches.
/// </summary>
public class StoppedSearchesDto
{
    /// <summary>
    /// Gets or sets how many episodes were taken out of the search.
    /// </summary>
    public int Stopped { get; set; }
}
