namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The YouTube tab's settings in the dashboard.
/// </summary>
public class YouTubeSettingsDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the YouTube tab is on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the YouTube Data API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OAuth client id.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a new OAuth client secret (never returned; empty keeps the saved one).
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a client secret is saved.
    /// </summary>
    public bool HasClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the YouTube searches allowed per day across all users.
    /// </summary>
    public int DailySearchCap { get; set; } = 50;

    /// <summary>
    /// Gets or sets the region for popular charts.
    /// </summary>
    public string RegionCode { get; set; } = "US";

    /// <summary>
    /// Gets or sets the YouTube Data API quota units used today (of 10,000).
    /// </summary>
    public int UnitsUsedToday { get; set; }

    /// <summary>
    /// Gets or sets the searches left today.
    /// </summary>
    public int SearchesLeftToday { get; set; }
}
