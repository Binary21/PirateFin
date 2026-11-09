using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Trailers saved next to downloaded movies and series: the first 30 seconds of the title's YouTube trailer in
/// 720p-1080p H.264, as &lt;title folder&gt;/trailers/trailer.mp4, which Jellyfin picks up as the title's trailer.
/// Downloads respect the minimum free space of each drive and can be turned off; saved ones keep playing.
/// </summary>
public interface ITrailerLibrary
{
    /// <summary>
    /// Queues a movie or series (an episode means its series) to get its trailer in the background.
    /// </summary>
    /// <param name="item">The movie, series or episode.</param>
    void Enqueue(BaseItem item);

    /// <summary>
    /// Saves the trailer of a movie or series now, when it's downloaded, has a YouTube trailer, hasn't got a trailer
    /// yet and its drive has room.
    /// </summary>
    /// <param name="item">The movie or series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    Task<TrailerSaveResult> SaveAsync(BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the downloaded movies and series that should get a trailer and haven't got one (nor failed recently).
    /// </summary>
    /// <returns>The movies and series.</returns>
    IReadOnlyList<BaseItem> GetMissing();

    /// <summary>
    /// Gets how many trailers PirateFin saved and their total size.
    /// </summary>
    /// <returns>The count and bytes.</returns>
    (int Count, long Bytes) GetSummary();

    /// <summary>
    /// Deletes every trailer PirateFin saved (trailers added by hand are left alone).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of trailers deleted.</returns>
    Task<int> DeleteAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What happened when saving a trailer.
/// </summary>
public enum TrailerSaveResult
{
    /// <summary>
    /// Saved.
    /// </summary>
    Saved,

    /// <summary>
    /// Nothing to do: trailers are turned off, it isn't downloaded, has no YouTube trailer or already has a trailer.
    /// </summary>
    Skipped,

    /// <summary>
    /// Its drive is below the minimum free space; tried again later.
    /// </summary>
    NoRoom,

    /// <summary>
    /// The trailer is gone from YouTube or isn't available in 720p or better.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The download failed; tried again later.
    /// </summary>
    Failed
}
