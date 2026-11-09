namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// The code a user enters at google.com/device to link their YouTube.
/// </summary>
public sealed class YouTubeDeviceCode
{
    /// <summary>
    /// Gets or sets the code to enter.
    /// </summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where to enter it.
    /// </summary>
    public string VerificationUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long the code works.
    /// </summary>
    public int ExpiresInSeconds { get; set; }

    /// <summary>
    /// Gets or sets how often to check whether it was entered.
    /// </summary>
    public int IntervalSeconds { get; set; }
}
