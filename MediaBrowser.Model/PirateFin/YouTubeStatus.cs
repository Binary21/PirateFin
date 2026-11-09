using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Whether the YouTube tab is on and how the user's account is linked.
/// </summary>
public sealed class YouTubeStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether an admin turned the YouTube tab on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user linked their YouTube.
    /// </summary>
    public bool Linked { get; set; }

    /// <summary>
    /// Gets or sets how it's linked: Google (signed in) or Channel (public subscriptions).
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// Gets or sets the linked channel's name.
    /// </summary>
    public string? ChannelTitle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether "Sign in with Google" is set up on the server.
    /// </summary>
    public bool GoogleAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether linking by channel id is set up on the server (API key).
    /// </summary>
    public bool ChannelIdAvailable { get; set; }

    /// <summary>
    /// Gets or sets when the user's feed was last refreshed.
    /// </summary>
    public DateTime? LastRefreshUtc { get; set; }

    /// <summary>
    /// Gets or sets why the last refresh failed, if it did.
    /// </summary>
    public string? LastError { get; set; }
}
