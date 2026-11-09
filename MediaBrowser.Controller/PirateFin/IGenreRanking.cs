using System;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Ranks the genres each user watches, from what they have played: a movie counts once it's watched at least halfway,
/// a series once at least three of its episodes are played.
/// </summary>
public interface IGenreRanking
{
    /// <summary>
    /// Gets a user's genre ranking for a library, with discovery genres from the other users.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <returns>The ranking; empty lists when the library or user doesn't exist.</returns>
    GenreRanking GetRanking(Guid userId, string library);
}
