namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// What a notification in a user's bell is about.
/// </summary>
public enum UserNotificationKind
{
    /// <summary>
    /// A new episode of a season the user follows is ready to watch.
    /// </summary>
    EpisodeReady,

    /// <summary>
    /// An admin closed one of the user's download requests.
    /// </summary>
    RequestClosed,

    /// <summary>
    /// A slow download the user started can be played now.
    /// </summary>
    DownloadReady
}
