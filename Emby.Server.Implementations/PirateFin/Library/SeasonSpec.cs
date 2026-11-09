using System.Globalization;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// The provider data for a placeholder season.
/// </summary>
/// <param name="Number">The season number.</param>
/// <param name="Overview">The overview.</param>
/// <param name="Genres">The genres.</param>
/// <param name="Subscribed">Whether a newly created season starts subscribed.</param>
public sealed record SeasonSpec(int Number, string Overview, string[] Genres, bool Subscribed)
{
    /// <summary>
    /// Gets the folder / display name.
    /// </summary>
    public string Title => "Season " + Number.ToString(CultureInfo.InvariantCulture);
}
