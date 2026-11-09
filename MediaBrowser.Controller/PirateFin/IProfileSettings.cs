using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Per profile (user) settings PirateFin adds: an optional 4-digit PIN asked on "Who's watching?", the kids flag,
/// and titles the user removed from Continue Watching / Next Up.
/// </summary>
public interface IProfileSettings
{
    /// <summary>
    /// Gets a value indicating whether the user has a PIN.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> if they have one.</returns>
    bool HasPin(Guid userId);

    /// <summary>
    /// Sets or replaces the user's PIN.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="pin">Four digits.</param>
    /// <exception cref="ArgumentException">The PIN isn't four digits.</exception>
    void SetPin(Guid userId, string pin);

    /// <summary>
    /// Removes the user's PIN.
    /// </summary>
    /// <param name="userId">The user id.</param>
    void RemovePin(Guid userId);

    /// <summary>
    /// Checks a PIN. After 5 wrong tries in a row, checks are refused for 5 minutes.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="pin">The PIN entered.</param>
    /// <returns>Whether it's right; <c>null</c> while locked out.</returns>
    bool? VerifyPin(Guid userId, string pin);

    /// <summary>
    /// Gets a value indicating whether the user has a kids profile.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> for a kids profile.</returns>
    bool IsKids(Guid userId);

    /// <summary>
    /// Records whether the user has a kids profile (the rating limit itself is the user's Jellyfin policy).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="isKids">Whether it's a kids profile.</param>
    void SetKids(Guid userId, bool isKids);

    /// <summary>
    /// Gets the movies and series the user removed from Continue Watching / Next Up.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The item ids.</returns>
    IReadOnlyList<Guid> GetHiddenFromContinueWatching(Guid userId);

    /// <summary>
    /// Removes a movie or series from the user's Continue Watching / Next Up until they play it again.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The movie or series id.</param>
    void HideFromContinueWatching(Guid userId, Guid itemId);

    /// <summary>
    /// Brings a movie or series back to Continue Watching / Next Up (the user played it).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The movie or series id.</param>
    /// <returns><c>true</c> if it was hidden.</returns>
    bool ShowInContinueWatching(Guid userId, Guid itemId);
}
