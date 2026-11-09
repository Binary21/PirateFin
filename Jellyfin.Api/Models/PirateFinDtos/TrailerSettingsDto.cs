namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The trailer settings and how much the saved trailers take up.
/// </summary>
public class TrailerSettingsDto
{
    /// <summary>
    /// Gets or sets a value indicating whether trailers are downloaded.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the number of trailers PirateFin saved.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets their total size in bytes.
    /// </summary>
    public long Bytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether missing trailers are being saved right now.
    /// </summary>
    public bool IsSaving { get; set; }
}
