namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// The provider data for a placeholder series.
/// </summary>
/// <param name="Title">The title; also the folder name.</param>
/// <param name="Overview">The overview.</param>
/// <param name="ImageUrl">The poster url shown until metadata is fetched.</param>
/// <param name="Year">The start year.</param>
/// <param name="Genres">The genres.</param>
/// <param name="AniListId">The AniList id, or empty.</param>
public sealed record SeriesSpec(string Title, string Overview, string ImageUrl, int? Year, string[] Genres, string AniListId);
