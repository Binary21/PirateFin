namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// Fingerprints of an item and of its children. A token differs from the previous one once something was saved
/// (metadata, images, a new child), so an open page knows which part of it to fetch again.
/// </summary>
public class ChangeTokenDto
{
    /// <summary>
    /// Gets or sets the token of the item itself.
    /// </summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token of the item's direct children (a series' seasons, a season's episodes).
    /// </summary>
    public string Children { get; set; } = string.Empty;
}
