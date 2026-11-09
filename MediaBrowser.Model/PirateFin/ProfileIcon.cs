namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A profile icon users can choose.
/// </summary>
public class ProfileIcon
{
    /// <summary>
    /// Gets or sets the image's file name within its pack.
    /// </summary>
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season the icon is from, when its pack spans several; otherwise <c>null</c>.
    /// </summary>
    public string? Season { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether new users may be given this icon at random.
    /// </summary>
    public bool Default { get; set; }
}
