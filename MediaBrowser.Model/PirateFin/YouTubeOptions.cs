namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// The YouTube tab's settings, loaded from the "YouTube" section of youtube.json. Off until an admin turns it on.
/// </summary>
public sealed class YouTubeOptions
{
    /// <summary>
    /// The name of the configuration section.
    /// </summary>
    public const string SectionName = "YouTube";

    /// <summary>
    /// Gets or sets a value indicating whether the YouTube tab is on.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the YouTube Data API key of the server's Google Cloud project (used for public channel subscriptions).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OAuth client id ("TVs and Limited Input devices") for "Sign in with Google".
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OAuth client secret, encrypted for the server's Windows account (DPAPI).
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of YouTube searches allowed per day across all users (each costs 100 quota units).
    /// </summary>
    public int DailySearchCap { get; set; } = 50;

    /// <summary>
    /// Gets or sets the region for YouTube's popular charts, e.g. US.
    /// </summary>
    public string RegionCode { get; set; } = "US";
}
