using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The name of an icon pack.
/// </summary>
public class IconPackNameDto
{
    /// <summary>
    /// Gets or sets the display name; the pack's folder is named after it.
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;
}
