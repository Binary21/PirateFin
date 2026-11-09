namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A YouTube channel.
/// </summary>
public sealed class YouTubeChannel
{
    /// <summary>
    /// Gets or sets the channel id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the avatar image.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Gets or sets the subscriber count (when public).
    /// </summary>
    public long? SubscriberCount { get; set; }

    /// <summary>
    /// Gets or sets the playlist of its uploads.
    /// </summary>
    public string? UploadsPlaylistId { get; set; }

    /// <summary>
    /// Gets or sets the @handle.
    /// </summary>
    public string? Handle { get; set; }

    /// <summary>
    /// Gets or sets the channel's description (on its page only).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the banner image (on its page only).
    /// </summary>
    public string? BannerUrl { get; set; }

    /// <summary>
    /// Gets or sets the number of public videos.
    /// </summary>
    public long? VideoCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user subscribes to it.
    /// </summary>
    public bool Subscribed { get; set; }
}
