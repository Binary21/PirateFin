namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A folder of a PirateFin library and the free space of its drive.
/// </summary>
public class StorageLocation
{
    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the folder.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the free space of the folder's drive, in bytes.
    /// </summary>
    public long FreeBytes { get; set; }

    /// <summary>
    /// Gets or sets the size of the folder's drive, in bytes.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the drive has less than the minimum free space.
    /// </summary>
    public bool IsFull { get; set; }
}
