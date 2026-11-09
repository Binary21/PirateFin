using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Reports what the episode release searches are doing (dashboard) and can pause or stop the back catalogue pass.
/// </summary>
public interface IReleaseSearchStatus
{
    /// <summary>
    /// Gets the status. The counts go over every episode waiting for a search, so they're recalculated at most once a
    /// minute.
    /// </summary>
    /// <returns>The status.</returns>
    ReleaseSearchStatus GetStatus();

    /// <summary>
    /// Stops the searches of the older episodes still waiting for their one search, of every show or one show. They
    /// keep no release, so they can't start downloading when opened until something searches them again.
    /// </summary>
    /// <param name="seriesId">The show; <c>null</c> for every show.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many episodes were taken out of the search.</returns>
    Task<int> StopOldSearchesAsync(Guid? seriesId, CancellationToken cancellationToken);

    /// <summary>
    /// Pauses the older episodes' searches, of every show or one show, until resumed. New episodes keep being searched.
    /// </summary>
    /// <param name="seriesId">The show; <c>null</c> for every show.</param>
    void PauseOldSearches(Guid? seriesId);

    /// <summary>
    /// Resumes paused old searches. Their search times move on by how long they were paused, so they don't all fall
    /// due at once.
    /// </summary>
    /// <param name="seriesId">The show; <c>null</c> for every show (shows paused on their own stay paused).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ResumeOldSearchesAsync(Guid? seriesId, CancellationToken cancellationToken);
}
