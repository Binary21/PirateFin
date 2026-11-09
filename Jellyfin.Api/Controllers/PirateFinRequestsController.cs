using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.PirateFinDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// PirateFin download requests: users ask an admin to find episodes PirateFin couldn't; admins answer them.
/// </summary>
[Route("PirateFin")]
[Authorize]
public class PirateFinRequestsController : BaseJellyfinApiController
{
    private readonly IDownloadRequests _requests;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IProfileSettings _profiles;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinRequestsController"/> class.
    /// </summary>
    /// <param name="requests">Instance of the <see cref="IDownloadRequests"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="profiles">Instance of the <see cref="IProfileSettings"/> interface.</param>
    public PirateFinRequestsController(IDownloadRequests requests, IUserManager userManager, ILibraryManager libraryManager, IProfileSettings profiles)
    {
        _requests = requests;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _profiles = profiles;
    }

    /// <summary>
    /// Asks an admin to find an episode PirateFin found no release for, or a season's episodes without one.
    /// </summary>
    /// <param name="request">The episode or season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The request.</response>
    /// <response code="400">Nothing to request, already requested, or the limit is reached (the reason is returned).</response>
    /// <response code="403">Kids profiles can't make requests.</response>
    /// <response code="404">No such item.</response>
    /// <returns>An <see cref="OkResult"/> containing the request.</returns>
    [HttpPost("Requests")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DownloadRequest>> CreateRequest([FromBody, Required] CreateRequestDto request, CancellationToken cancellationToken)
    {
        if (_profiles.IsKids(RequestHelpers.GetUserId(User, null)))
        {
            return Forbid();
        }

        var user = _userManager.GetUserById(RequestHelpers.GetUserId(User, null));
        if (user is null || _libraryManager.GetItemById<MediaBrowser.Controller.Entities.BaseItem>(request.ItemId, user) is not { } item)
        {
            return NotFound();
        }

        try
        {
            return await _requests.CreateAsync(user, item, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets the signed in user's requests and how many more they may make.
    /// </summary>
    /// <response code="200">The requests.</response>
    /// <returns>An <see cref="OkResult"/> containing the requests.</returns>
    [HttpGet("Requests/Mine")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MyRequestsDto> GetMyRequests()
    {
        var userId = RequestHelpers.GetUserId(User, null);
        var quota = _requests.GetQuota(userId);
        return new MyRequestsDto
        {
            Requests = _requests.GetForUser(userId),
            Limit = quota.Limit,
            Open = quota.Open,
            Remaining = quota.Remaining
        };
    }

    /// <summary>
    /// Withdraws one of the signed in user's open requests, freeing the slot.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    /// <response code="204">Cancelled.</response>
    /// <response code="404">No such open request of theirs.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Requests/{requestId}/Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelRequest([FromRoute, Required] Guid requestId)
        => _requests.Cancel(requestId, RequestHelpers.GetUserId(User, null)) ? NoContent() : NotFound();

    /// <summary>
    /// Gets the requests (admins).
    /// </summary>
    /// <param name="includeClosed">Whether to include closed and cancelled requests.</param>
    /// <response code="200">The requests, newest first.</response>
    /// <returns>An <see cref="OkResult"/> containing the requests.</returns>
    [HttpGet("Requests")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DownloadRequest>> GetRequests([FromQuery] bool includeClosed = false)
        => Ok(_requests.GetAll(includeClosed));

    /// <summary>
    /// Gets the number of open requests (admins), for the dashboard badge.
    /// </summary>
    /// <response code="200">The count.</response>
    /// <returns>An <see cref="OkResult"/> containing the count.</returns>
    [HttpGet("Requests/OpenCount")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> GetOpenRequestCount() => _requests.CountOpen();

    /// <summary>
    /// Gives a requested episode the release an admin found, and starts downloading it.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="episodeId">The episode id.</param>
    /// <param name="magnet">The magnet link.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Downloading.</response>
    /// <response code="400">Not a magnet link.</response>
    /// <response code="404">No such request or episode.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Requests/{requestId}/Episodes/{episodeId}/Magnet")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetMagnet(
        [FromRoute, Required] Guid requestId,
        [FromRoute, Required] Guid episodeId,
        [FromBody, Required] MagnetDto magnet,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _requests.SetMagnetAsync(requestId, episodeId, magnet.MagnetUrl, cancellationToken).ConfigureAwait(false)
                ? NoContent()
                : NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Closes a request as fulfilled or not found (freeing the user's slot) and emails the user.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="close">The outcome and a note.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Closed.</response>
    /// <response code="400">Not a closing status.</response>
    /// <response code="404">No such open request.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Requests/{requestId}/Close")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> CloseRequest([FromRoute, Required] Guid requestId, [FromBody, Required] CloseRequestDto close, CancellationToken cancellationToken)
    {
        try
        {
            return await _requests.CloseAsync(requestId, close.Status, close.Note, cancellationToken).ConfigureAwait(false)
                ? NoContent()
                : NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets the request limit new users get (admins).
    /// </summary>
    /// <response code="200">The limit.</response>
    /// <returns>An <see cref="OkResult"/> containing the limit.</returns>
    [HttpGet("Requests/DefaultLimit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RequestLimitDto> GetDefaultLimit() => new RequestLimitDto { Limit = _requests.DefaultLimit };

    /// <summary>
    /// Sets the request limit users get unless they have their own (admins); -1 for no limit.
    /// </summary>
    /// <param name="limit">The limit.</param>
    /// <response code="204">Saved.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Requests/DefaultLimit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SetDefaultLimit([FromBody, Required] RequestLimitDto limit)
    {
        _requests.DefaultLimit = limit.Limit ?? -1;
        return NoContent();
    }

    /// <summary>
    /// Gets a user's own request limit (admins); <c>null</c> when they get the default.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <response code="200">The limit.</response>
    /// <returns>An <see cref="OkResult"/> containing the limit.</returns>
    [HttpGet("Users/{userId}/RequestLimit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RequestLimitDto> GetUserLimit([FromRoute, Required] Guid userId)
        => new RequestLimitDto { Limit = _requests.GetLimit(userId) };

    /// <summary>
    /// Sets a user's request limit (admins); -1 for no limit, <c>null</c> for the default.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="limit">The limit.</param>
    /// <response code="204">Saved.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Users/{userId}/RequestLimit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SetUserLimit([FromRoute, Required] Guid userId, [FromBody, Required] RequestLimitDto limit)
    {
        _requests.SetLimit(userId, limit.Limit);
        return NoContent();
    }
}
