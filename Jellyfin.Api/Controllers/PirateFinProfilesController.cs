using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.PirateFinDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// PirateFin profiles: the PIN asked on "Who's watching?", kids profiles, and titles removed from Continue Watching.
/// </summary>
[Route("PirateFin/Users")]
[Authorize]
public class PirateFinProfilesController : BaseJellyfinApiController
{
    private readonly IProfileSettings _profiles;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinProfilesController"/> class.
    /// </summary>
    /// <param name="profiles">Instance of the <see cref="IProfileSettings"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public PirateFinProfilesController(IProfileSettings profiles, IUserManager userManager, ILibraryManager libraryManager)
    {
        _profiles = profiles;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets a profile's PIN and kids settings (the user themself or an admin).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <response code="200">The settings.</response>
    /// <response code="403">Not allowed.</response>
    /// <response code="404">No such user.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="ProfileSettingsDto"/>.</returns>
    [HttpGet("{userId}/Profile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ProfileSettingsDto> GetProfile([FromRoute, Required] Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(HttpContext.User, user, true))
        {
            return Forbid();
        }

        return new ProfileSettingsDto
        {
            HasPin = _profiles.HasPin(userId),
            IsKids = _profiles.IsKids(userId),
            MaxParentalRating = _userManager.GetUserDto(user).Policy?.MaxParentalRating
        };
    }

    /// <summary>
    /// Sets the signed in user's 4-digit PIN. A kids profile's PIN (which guards leaving it) can only be changed with the
    /// current one.
    /// </summary>
    /// <param name="userId">The user id (must be the signed in user).</param>
    /// <param name="request">The PIN, and for a kids profile the current PIN.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Not 4 digits, or the current PIN is wrong.</response>
    /// <response code="403">Not the signed in user.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{userId}/Pin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult SetPin([FromRoute, Required] Guid userId, [FromBody, Required] PinDto request)
    {
        if (!userId.Equals(RequestHelpers.GetUserId(User, null)))
        {
            return Forbid();
        }

        if (_profiles.IsKids(userId) && _profiles.HasPin(userId))
        {
            var current = _profiles.VerifyPin(userId, request.CurrentPin ?? string.Empty);
            if (current != true)
            {
                return BadRequest(current is null ? "Too many wrong tries. Try again in 5 minutes." : "The current PIN is wrong.");
            }
        }

        try
        {
            _profiles.SetPin(userId, request.Pin);
        }
        catch (ArgumentException)
        {
            return BadRequest("A PIN is 4 digits.");
        }

        return NoContent();
    }

    /// <summary>
    /// Removes a profile's PIN (the user themself, or an admin for a forgotten PIN). A kids profile's PIN only by an admin.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <response code="204">Removed.</response>
    /// <response code="403">Not allowed.</response>
    /// <response code="404">No such user.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{userId}/Pin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemovePin([FromRoute, Required] Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(HttpContext.User, user, true)
            || (_profiles.IsKids(userId) && !User.IsInRole(UserRoles.Administrator)))
        {
            return Forbid();
        }

        _profiles.RemovePin(userId);
        return NoContent();
    }

    /// <summary>
    /// Checks a profile's PIN, with that profile's own sign in (after 5 wrong tries, checks pause for 5 minutes).
    /// </summary>
    /// <param name="userId">The user id (must be the signed in user).</param>
    /// <param name="request">The PIN entered.</param>
    /// <response code="200">Whether it's right.</response>
    /// <response code="403">Not the signed in user.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="PinCheckDto"/>.</returns>
    [HttpPost("{userId}/Pin/Verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<PinCheckDto> VerifyPin([FromRoute, Required] Guid userId, [FromBody, Required] PinDto request)
    {
        if (!userId.Equals(RequestHelpers.GetUserId(User, null)))
        {
            return Forbid();
        }

        var result = _profiles.VerifyPin(userId, request.Pin);
        return new PinCheckDto { Valid = result == true, LockedOut = result is null };
    }

    /// <summary>
    /// Makes a profile a kids profile or a regular one again (Parental Control, after its rating limit was saved), and
    /// sets the kids profile's PIN. A kids profile needs a rating limit.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="request">Kids on / off and a new PIN.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">No rating limit for a kids profile, or the PIN isn't 4 digits.</response>
    /// <response code="404">No such user.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{userId}/Kids")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetKids([FromRoute, Required] Guid userId, [FromBody, Required] KidsProfileDto request)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (request.IsKids && _userManager.GetUserDto(user).Policy?.MaxParentalRating is null)
        {
            return BadRequest("Choose the Maximum allowed parental rating for a kids profile.");
        }

        if (request.IsKids && !string.IsNullOrEmpty(request.Pin))
        {
            try
            {
                _profiles.SetPin(userId, request.Pin);
            }
            catch (ArgumentException)
            {
                return BadRequest("A PIN is 4 digits.");
            }
        }

        _profiles.SetKids(userId, request.IsKids);
        return NoContent();
    }

    /// <summary>
    /// Gets the movies and series the signed in user removed from Continue Watching / Next Up.
    /// </summary>
    /// <response code="200">The item ids.</response>
    /// <returns>An <see cref="OkResult"/> containing the ids.</returns>
    [HttpGet("Me/ContinueWatching/Hidden")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Guid>> GetHiddenFromContinueWatching()
        => Ok(_profiles.GetHiddenFromContinueWatching(RequestHelpers.GetUserId(User, null)));

    /// <summary>
    /// Removes a title from the signed in user's Continue Watching and Next Up (an episode removes its series) until they
    /// play it again. What's marked watched doesn't change.
    /// </summary>
    /// <param name="request">The movie, series or episode.</param>
    /// <response code="204">Removed.</response>
    /// <response code="404">No such item.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Me/ContinueWatching/Hide")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult HideFromContinueWatching([FromBody, Required] HideFromContinueWatchingDto request)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound();
        }

        var titleId = item is Episode episode && !episode.SeriesId.Equals(Guid.Empty) ? episode.SeriesId : item.Id;
        _profiles.HideFromContinueWatching(RequestHelpers.GetUserId(User, null), titleId);
        return NoContent();
    }
}
