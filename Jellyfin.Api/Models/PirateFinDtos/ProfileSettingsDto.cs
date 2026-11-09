namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A profile's PIN and kids settings.
/// </summary>
public class ProfileSettingsDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the profile has a PIN.
    /// </summary>
    public bool HasPin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's a kids profile.
    /// </summary>
    public bool IsKids { get; set; }

    /// <summary>
    /// Gets or sets the highest parental rating the profile can watch (the user's Jellyfin policy).
    /// </summary>
    public int? MaxParentalRating { get; set; }
}
