using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A new password.
/// </summary>
public class NewPasswordDto
{
    /// <summary>
    /// Gets or sets the new password.
    /// </summary>
    [Required]
    public string NewPassword { get; set; } = string.Empty;
}
