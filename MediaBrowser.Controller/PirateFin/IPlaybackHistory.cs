using System;
using System.Collections.Generic;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// PirateFin's log of what each user watched and searched for, used by the dashboard's user insights. Kept for
/// two years.
/// </summary>
public interface IPlaybackHistory
{
    /// <summary>
    /// Saves a new or updated playback session.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="id">The id this method returned for the session before; <c>null</c> for a new one.</param>
    /// <returns>The session's id.</returns>
    long SaveSession(PlaybackSessionRecord session, long? id);

    /// <summary>
    /// Records a search for a show, anime or movie. While the user keeps typing, the search replaces their previous one.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="library">The library searched.</param>
    /// <param name="query">The search text.</param>
    /// <param name="results">The number of results.</param>
    void RecordSearch(Guid userId, string library, string query, int results);

    /// <summary>
    /// Gets a user's playback sessions that started in a time range, oldest first.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="fromUtc">The start of the range.</param>
    /// <param name="toUtc">The end of the range.</param>
    /// <returns>The sessions.</returns>
    IReadOnlyList<PlaybackSessionRecord> GetSessions(Guid userId, DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// Gets a user's searches in a time range, newest first.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="fromUtc">The start of the range.</param>
    /// <param name="toUtc">The end of the range.</param>
    /// <returns>The searches.</returns>
    IReadOnlyList<SearchRecord> GetSearches(Guid userId, DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// Deletes sessions and searches older than the retention period (two years).
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    int DeleteExpired();
}
