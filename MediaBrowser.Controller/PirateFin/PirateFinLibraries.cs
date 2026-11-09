using System;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Names of the libraries PirateFin generates items into.
/// </summary>
public static class PirateFinLibraries
{
    /// <summary>
    /// The TV shows library.
    /// </summary>
    public const string Shows = "Shows";

    /// <summary>
    /// The anime library.
    /// </summary>
    public const string Anime = "Anime";

    /// <summary>
    /// The movies library.
    /// </summary>
    public const string Movies = "Movies";

    /// <summary>
    /// Normalizes a user supplied library name to one of the known library names.
    /// </summary>
    /// <param name="library">The library name.</param>
    /// <returns>The canonical name, or <c>null</c> when unknown.</returns>
    public static string? Normalize(string? library)
    {
        if (string.Equals(library, Shows, StringComparison.OrdinalIgnoreCase))
        {
            return Shows;
        }

        if (string.Equals(library, Anime, StringComparison.OrdinalIgnoreCase))
        {
            return Anime;
        }

        if (string.Equals(library, Movies, StringComparison.OrdinalIgnoreCase))
        {
            return Movies;
        }

        return null;
    }
}
