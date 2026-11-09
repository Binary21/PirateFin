namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A torrent release returned by an indexer search.
/// </summary>
public sealed record TorrentCandidate
{
    /// <summary>
    /// Gets the release title as reported by the indexer.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the magnet (or torrent file) url.
    /// </summary>
    public string MagnetUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of seeders.
    /// </summary>
    public int Seeders { get; init; }

    /// <summary>
    /// Gets the size of the release in bytes, 0 when unknown.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets the season number parsed from the title, if any.
    /// </summary>
    public int? Season { get; init; }

    /// <summary>
    /// Gets the episode number parsed from the title, if any.
    /// </summary>
    public int? Episode { get; init; }

    /// <summary>
    /// Gets the release author the candidate was found with, or <c>null</c> for an author-less fallback search.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets the anime language of the preferred group list the candidate was found with, or <c>null</c> for TV and
    /// author-less fallback searches.
    /// </summary>
    public ReleaseLanguage? Language { get; init; }
}
