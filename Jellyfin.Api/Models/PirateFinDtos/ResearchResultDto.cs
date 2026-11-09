namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// What searching a series or season again did.
/// </summary>
public class ResearchResultDto
{
    /// <summary>
    /// Gets or sets how many episodes will be searched again.
    /// </summary>
    public int Searching { get; set; }

    /// <summary>
    /// Gets or sets how many of those were downloaded and had their file deleted to be replaced.
    /// </summary>
    public int Replaced { get; set; }

    /// <summary>
    /// Gets or sets how many were left alone because they are downloading right now.
    /// </summary>
    public int SkippedDownloading { get; set; }
}
