using MediaBrowser.Common.Api;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Filling in the Shows / Anime libraries: every series' missing seasons and episodes, metadata and images, and
/// release searches spaced out over time. The same runs every night for recent and airing series.
/// </summary>
[Route("PirateFin/Libraries/Populate")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PirateFinPopulateController : BaseJellyfinApiController
{
    private readonly ILibraryPopulator _populator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinPopulateController"/> class.
    /// </summary>
    /// <param name="populator">Instance of the <see cref="ILibraryPopulator"/> interface.</param>
    public PirateFinPopulateController(ILibraryPopulator populator)
    {
        _populator = populator;
    }

    /// <summary>
    /// Gets where the current (or last) run stands.
    /// </summary>
    /// <response code="200">The status.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="PopulateStatus"/>.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PopulateStatus> GetStatus() => _populator.GetStatus();

    /// <summary>
    /// Starts filling in every series in the Shows and Anime libraries, in the background.
    /// </summary>
    /// <response code="202">Started.</response>
    /// <response code="409">A run is already going on.</response>
    /// <returns>An <see cref="AcceptedResult"/>.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult Start()
        => _populator.StartFullRun() ? Accepted() : Conflict("Filling in the libraries is already going on.");
}
