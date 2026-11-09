using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// One time a user watched a movie or an episode, from PirateFin's playback log.
/// </summary>
public sealed class PlaybackSessionRecord
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the movie or episode id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the series id, for an episode.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the item type: Movie or Episode.
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the library the item is in (Shows, Anime, Movies or another library).
    /// </summary>
    public string? Library { get; set; }

    /// <summary>
    /// Gets or sets the movie or episode name.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the series name, for an episode.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the season number, for an episode.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number, for an episode.
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets when playback started.
    /// </summary>
    public DateTime StartUtc { get; set; }

    /// <summary>
    /// Gets or sets when playback stopped (or was last seen, for a session still running).
    /// </summary>
    public DateTime EndUtc { get; set; }

    /// <summary>
    /// Gets or sets the seconds actually watched, not counting pauses.
    /// </summary>
    public long WatchedSeconds { get; set; }

    /// <summary>
    /// Gets or sets the position playback stopped at.
    /// </summary>
    public long? PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the item's run time.
    /// </summary>
    public long? RuntimeTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item was played to the end.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// Gets or sets the app used, e.g. Jellyfin Web.
    /// </summary>
    public string? Client { get; set; }

    /// <summary>
    /// Gets or sets the device used.
    /// </summary>
    public string? Device { get; set; }
}
