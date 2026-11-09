namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// Where a Google sign in is.
/// </summary>
public class YouTubeLinkStateDto
{
    /// <summary>
    /// Gets or sets pending, linked, denied or expired.
    /// </summary>
    public string State { get; set; } = string.Empty;
}
