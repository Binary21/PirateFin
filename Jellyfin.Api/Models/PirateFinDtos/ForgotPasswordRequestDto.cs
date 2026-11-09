using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The user name entered on the forgot password page.
/// </summary>
public class ForgotPasswordRequestDto
{
    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;
}
