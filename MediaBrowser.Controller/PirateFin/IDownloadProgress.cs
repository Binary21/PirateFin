using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// An episode or movie's download status as its Play button shows it; the same for the HTTP endpoint and the
/// WebSocket pushes.
/// </summary>
public interface IDownloadProgress
{
    /// <summary>
    /// Gets a value indicating whether enough of a video is on disk to start playing.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>Whether it can be played.</returns>
    bool IsPlayable(Video video);

    /// <summary>
    /// Gets a video's download status.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>The status.</returns>
    DownloadProgressInfo Get(Video video);
}
