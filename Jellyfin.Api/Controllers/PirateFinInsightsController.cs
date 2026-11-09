using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Api.Helpers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// PirateFin user insights: what each user watches, for the dashboard and their genre rows.
/// </summary>
[Route("PirateFin")]
[Authorize]
public class PirateFinInsightsController : BaseJellyfinApiController
{
    private readonly IGenreRanking _genreRanking;
    private readonly IUserInsights _insights;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinInsightsController"/> class.
    /// </summary>
    /// <param name="genreRanking">Instance of the <see cref="IGenreRanking"/> interface.</param>
    /// <param name="insights">Instance of the <see cref="IUserInsights"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public PirateFinInsightsController(IGenreRanking genreRanking, IUserInsights insights, IUserManager userManager)
    {
        _genreRanking = genreRanking;
        _insights = insights;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets what a user watches and asks for: watch time, when they watch, genres, titles, drop-offs, subscriptions,
    /// requests, searches that found nothing and suggestions for the admin.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="utcOffsetMinutes">The viewer's offset from UTC in minutes (e.g. -240 for EDT), for days and hours.</param>
    /// <response code="200">The insights.</response>
    /// <response code="404">No such user.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserInsights"/>.</returns>
    [HttpGet("Users/{userId}/Insights")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserInsights> GetInsights([FromRoute, Required] Guid userId, [FromQuery] int utcOffsetMinutes = 0)
    {
        var offset = TimeSpan.FromMinutes(Math.Clamp(utcOffsetMinutes, -14 * 60, 14 * 60));
        var insights = _insights.GetInsights(userId, offset);
        return insights is null ? NotFound() : insights;
    }

    /// <summary>
    /// Gets each user's watch time this week and top genre, for the dashboard's Users list.
    /// </summary>
    /// <response code="200">One summary per user.</response>
    /// <returns>An <see cref="OkResult"/> containing the summaries.</returns>
    [HttpGet("Insights/Summary")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<UserInsightSummary>> GetSummaries() => Ok(_insights.GetSummaries());

    /// <summary>
    /// Gets a user's genres in a library, most watched first, and discovery genres other users watch a lot.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <response code="200">The ranking.</response>
    /// <response code="400">Unknown library.</response>
    /// <response code="403">Not allowed to see this user's ranking.</response>
    /// <response code="404">No such user.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="GenreRanking"/>.</returns>
    [HttpGet("Users/{userId}/Genres")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<GenreRanking> GetGenreRanking([FromRoute, Required] Guid userId, [FromQuery, Required] string library)
    {
        var normalized = PirateFinLibraries.Normalize(library);
        if (normalized is null)
        {
            return BadRequest("Library must be Shows, Anime or Movies.");
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        // The user themself (for their genre rows) or an admin.
        if (!RequestHelpers.AssertCanUpdateUser(HttpContext.User, user, true))
        {
            return Forbid();
        }

        return _genreRanking.GetRanking(userId, normalized);
    }
}
