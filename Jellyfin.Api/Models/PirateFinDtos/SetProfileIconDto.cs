using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The icon to make a user's profile image.
/// </summary>
public class SetProfileIconDto
{
    /// <summary>
    /// Gets or sets the pack id.
    /// </summary>
    [Required]
    public string PackId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the icon's file name.
    /// </summary>
    [Required]
    public string File { get; set; } = string.Empty;
}
