namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The address the buttons in emails link to.
/// </summary>
public class EmailPublicAddressDto
{
    /// <summary>
    /// Gets or sets the address, e.g. https://piratefin.duckdns.org; empty for no buttons.
    /// </summary>
    public string PublicAddress { get; set; } = string.Empty;
}
