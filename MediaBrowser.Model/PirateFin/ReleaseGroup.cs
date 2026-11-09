namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A preferred release group and the words that mark its audio / subtitle type.
/// </summary>
public sealed class ReleaseGroup
{
    /// <summary>
    /// Gets or sets the release group name, e.g. "Yameii" or "[SubsPlease]".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language indicator, e.g. "English Dub" or "Multi-Subs". It is added to the search query, and a
    /// release must contain every one of its words. Empty adds nothing.
    /// </summary>
    public string Indicator { get; set; } = string.Empty;
}
