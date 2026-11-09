using System;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// The provider data for a placeholder episode.
/// </summary>
/// <param name="Number">The episode number.</param>
/// <param name="Title">The episode title, or empty.</param>
/// <param name="Overview">The overview.</param>
/// <param name="FirstAiring">The air time in UTC.</param>
/// <param name="AlternateTitle">The romaji title used for torrent searches.</param>
/// <param name="ImageUrl">The poster url shown until metadata is fetched.</param>
/// <param name="MagnetUrl">A known magnet url, or empty.</param>
/// <param name="StillUrl">The episode's own image (replaces a season / series poster on existing placeholders), or empty.</param>
public sealed record EpisodeSpec(int Number, string Title, string Overview, DateTime? FirstAiring, string AlternateTitle, string ImageUrl, string MagnetUrl, string StillUrl = "");
