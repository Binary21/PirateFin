using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A set of profile icons, e.g. the characters of one show. Stored as a folder holding the icon images.
/// </summary>
public class IconPack
{
    /// <summary>
    /// Gets or sets the id: the name of the pack's folder.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the icons, in display order.
    /// </summary>
    public IReadOnlyList<ProfileIcon> Icons { get; set; } = [];
}
