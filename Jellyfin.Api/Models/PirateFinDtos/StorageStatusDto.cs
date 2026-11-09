namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// Whether an item's downloads are paused because its library's storage is full.
/// </summary>
public class StorageStatusDto
{
    /// <summary>
    /// Gets or sets a value indicating whether no drive of the item's library has room, so its downloads wait.
    /// </summary>
    public bool Paused { get; set; }
}
