using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A release an admin found for a requested episode.
/// </summary>
public class MagnetDto
{
    /// <summary>
    /// Gets or sets the magnet link.
    /// </summary>
    [Required]
    public string MagnetUrl { get; set; } = string.Empty;
}
