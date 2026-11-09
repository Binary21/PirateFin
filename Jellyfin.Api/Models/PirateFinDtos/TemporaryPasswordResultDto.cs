namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// Whether a temporary password was emailed.
/// </summary>
public class TemporaryPasswordResultDto
{
    /// <summary>
    /// Gets or sets a value indicating whether an email was sent; <c>false</c> when the user has no email address (or
    /// there is no such user), so Jellyfin's own reset applies.
    /// </summary>
    public bool Emailed { get; set; }
}
