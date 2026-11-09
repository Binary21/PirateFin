using System;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Pauses of the back catalogue release searches (older episodes), for every show or one show. New episodes are never
/// paused. Kept until resumed, across restarts.
/// </summary>
public interface IReleaseSearchPauses
{
    /// <summary>
    /// Gets when the old searches of every show were paused, in UTC; <c>null</c> when they aren't.
    /// </summary>
    DateTime? AllPausedUtc { get; }

    /// <summary>
    /// Gets when the old searches of a show were paused, in UTC.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <returns>When it was paused; <c>null</c> when it isn't (on its own).</returns>
    DateTime? GetShowPausedUtc(Guid seriesId);

    /// <summary>
    /// Gets a value indicating whether a show's old searches are paused, on their own or with every show's.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <returns>Whether they're paused.</returns>
    bool IsPaused(Guid seriesId);

    /// <summary>
    /// Pauses the old searches of every show.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    void PauseAll(DateTime utcNow);

    /// <summary>
    /// Resumes the old searches of every show (shows paused on their own stay paused).
    /// </summary>
    /// <returns>When they were paused; <c>null</c> when they weren't.</returns>
    DateTime? ResumeAll();

    /// <summary>
    /// Pauses a show's old searches.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="utcNow">The current time.</param>
    void PauseShow(Guid seriesId, DateTime utcNow);

    /// <summary>
    /// Resumes a show's old searches.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <returns>When it was paused; <c>null</c> when it wasn't.</returns>
    DateTime? ResumeShow(Guid seriesId);
}
