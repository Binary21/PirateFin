namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A profile PIN.
/// </summary>
public class PinDto
{
    /// <summary>
    /// Gets or sets the 4 digits.
    /// </summary>
    public string Pin { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current PIN, needed to change a kids profile's PIN.
    /// </summary>
    public string? CurrentPin { get; set; }
}
