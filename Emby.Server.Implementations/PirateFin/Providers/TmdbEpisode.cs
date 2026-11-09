using System;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// An episode of a TMDB show.
/// </summary>
/// <param name="Season">The TMDB season number.</param>
/// <param name="Number">The episode number in that season.</param>
/// <param name="Name">The title.</param>
/// <param name="Overview">The description.</param>
/// <param name="AirDate">The air date (a day), or <c>null</c> when not announced.</param>
/// <param name="StillUrl">The still image url, or empty.</param>
public sealed record TmdbEpisode(int Season, int Number, string Name, string Overview, DateTime? AirDate, string StillUrl);
