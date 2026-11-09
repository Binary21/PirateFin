using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// Request to add an internet search result to the library.
/// </summary>
public class OpenSearchResultDto
{
    /// <summary>
    /// Gets or sets the library the result came from (Shows or Anime).
    /// </summary>
    [Required]
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the result key.
    /// </summary>
    [Required]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the search text the result came from.
    /// </summary>
    [Required]
    public string Query { get; set; } = string.Empty;
}
