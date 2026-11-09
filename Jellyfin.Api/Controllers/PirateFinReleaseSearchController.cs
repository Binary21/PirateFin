using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.PirateFinDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The episode release searches (Jackett): what's being searched, what's left, by show, and what recent searches found.
/// The older episodes' searches (the back catalogue pass) can be paused, resumed or stopped, for every show or one.
/// </summary>
[Route("PirateFin/ReleaseSearches")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PirateFinReleaseSearchController : BaseJellyfinApiController
{
    private readonly IReleaseSearchStatus _status;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinReleaseSearchController"/> class.
    /// </summary>
    /// <param name="status">Instance of the <see cref="IReleaseSearchStatus"/> interface.</param>
    public PirateFinReleaseSearchController(IReleaseSearchStatus status)
    {
        _status = status;
    }

    /// <summary>
    /// Gets the status. The counts are recalculated at most once a minute.
    /// </summary>
    /// <response code="200">The status.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="ReleaseSearchStatus"/>.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ReleaseSearchStatus> GetStatus() => _status.GetStatus();

    /// <summary>
    /// Stops the searches of the older episodes still waiting for their one search (the back catalogue pass). They
    /// keep no release, so they can't start downloading when opened until something searches them again.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">How many episodes were taken out of the search.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="StoppedSearchesDto"/>.</returns>
    [HttpPost("StopOld")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<StoppedSearchesDto>> StopOldSearches(CancellationToken cancellationToken)
        => new StoppedSearchesDto { Stopped = await _status.StopOldSearchesAsync(null, cancellationToken).ConfigureAwait(false) };

    /// <summary>
    /// Pauses the older episodes' searches of every show until resumed. New episodes keep being searched.
    /// </summary>
    /// <response code="204">Paused.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("PauseOld")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult PauseOldSearches()
    {
        _status.PauseOldSearches(null);
        return NoContent();
    }

    /// <summary>
    /// Resumes the older episodes' searches of every show (shows paused on their own stay paused).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Resumed.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("ResumeOld")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ResumeOldSearches(CancellationToken cancellationToken)
    {
        await _status.ResumeOldSearchesAsync(null, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Stops a show's older episodes' searches.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">How many episodes were taken out of the search.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="StoppedSearchesDto"/>.</returns>
    [HttpPost("Shows/{seriesId}/StopOld")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<StoppedSearchesDto>> StopShowOldSearches([FromRoute, Required] Guid seriesId, CancellationToken cancellationToken)
        => new StoppedSearchesDto { Stopped = await _status.StopOldSearchesAsync(seriesId, cancellationToken).ConfigureAwait(false) };

    /// <summary>
    /// Pauses a show's older episodes' searches until resumed. Its new episodes keep being searched.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <response code="204">Paused.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Shows/{seriesId}/PauseOld")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult PauseShowOldSearches([FromRoute, Required] Guid seriesId)
    {
        _status.PauseOldSearches(seriesId);
        return NoContent();
    }

    /// <summary>
    /// Resumes a show's older episodes' searches.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Resumed.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Shows/{seriesId}/ResumeOld")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ResumeShowOldSearches([FromRoute, Required] Guid seriesId, CancellationToken cancellationToken)
    {
        await _status.ResumeOldSearchesAsync(seriesId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
