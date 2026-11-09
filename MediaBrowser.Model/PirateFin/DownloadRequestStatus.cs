namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Where a <see cref="DownloadRequest"/> stands.
/// </summary>
public enum DownloadRequestStatus
{
    /// <summary>
    /// Waiting for an admin; counts against the user's limit.
    /// </summary>
    Open = 0,

    /// <summary>
    /// The admin found it.
    /// </summary>
    Fulfilled = 1,

    /// <summary>
    /// The admin couldn't find it either.
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// The user withdrew it.
    /// </summary>
    Cancelled = 3
}
