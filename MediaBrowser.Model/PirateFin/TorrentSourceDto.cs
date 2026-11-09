namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A fallback download source offered to the user for an item.
/// </summary>
public sealed class TorrentSourceDto
{
    /// <summary>
    /// Gets or sets the index used to select this source.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the release title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of seeders.
    /// </summary>
    public int Seeders { get; set; }

    /// <summary>
    /// Gets or sets the release size in bytes, 0 when unknown.
    /// </summary>
    public long SizeBytes { get; set; }
}
