namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// Kids profile on / off, and the PIN that lets someone leave it. The rating limit is the user's parental control
/// setting ("Maximum allowed parental rating"), saved before this.
/// </summary>
public class KidsProfileDto
{
    /// <summary>
    /// Gets or sets a value indicating whether it's a kids profile.
    /// </summary>
    public bool IsKids { get; set; }

    /// <summary>
    /// Gets or sets a new 4-digit PIN for the kids profile; <c>null</c> or empty keeps the current one.
    /// </summary>
    public string? Pin { get; set; }
}
