using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// Where to send a test email.
/// </summary>
public class TestEmailDto
{
    /// <summary>
    /// Gets or sets the recipient's address.
    /// </summary>
    [Required]
    public string To { get; set; } = string.Empty;
}
