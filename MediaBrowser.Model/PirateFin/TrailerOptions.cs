namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Trailers saved next to downloaded movies and series, loaded from the "Trailers" section of trailers.json.
/// </summary>
public sealed class TrailerOptions
{
    /// <summary>
    /// The name of the configuration section.
    /// </summary>
    public const string SectionName = "Trailers";

    /// <summary>
    /// Gets or sets a value indicating whether trailers are downloaded. Saved trailers keep playing when this is off.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
