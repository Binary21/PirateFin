namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The result of checking a PIN.
/// </summary>
public class PinCheckDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the PIN is right.
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether checks are paused after too many wrong tries.
    /// </summary>
    public bool LockedOut { get; set; }
}
