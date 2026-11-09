namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A hint for the admin, e.g. "Watches a lot of Thriller but the Shows library only has 4 Thriller titles".
/// </summary>
public sealed class InsightSuggestion
{
    /// <summary>
    /// Gets or sets the kind: ThinGenre, NothingFound, GenreDropOffs, WaitingRequest or Inactive.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text shown to the admin.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
