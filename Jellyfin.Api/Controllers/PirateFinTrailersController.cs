using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.PirateFinDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The dashboard's trailer settings: turning trailer downloads on and off, freeing their space, and saving the missing
/// ones now.
/// </summary>
[Route("PirateFin/Trailers")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PirateFinTrailersController : BaseJellyfinApiController
{
    // SaveTrailersTask.TaskKey (the API can't reference the implementation).
    private const string SaveTrailersTaskKey = "PirateFinSaveTrailers";

    private readonly ITrailerLibrary _trailers;
    private readonly IPirateFinConfigurationStore _configurationStore;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinTrailersController"/> class.
    /// </summary>
    /// <param name="trailers">Instance of the <see cref="ITrailerLibrary"/> interface.</param>
    /// <param name="configurationStore">Instance of the <see cref="IPirateFinConfigurationStore"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    public PirateFinTrailersController(ITrailerLibrary trailers, IPirateFinConfigurationStore configurationStore, ITaskManager taskManager)
    {
        _trailers = trailers;
        _configurationStore = configurationStore;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Gets whether trailers are downloaded, and how many PirateFin saved and how much space they use.
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="TrailerSettingsDto"/>.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TrailerSettingsDto> GetSettings()
    {
        var (count, bytes) = _trailers.GetSummary();
        return new TrailerSettingsDto
        {
            Enabled = _configurationStore.GetTrailers().Enabled,
            Count = count,
            Bytes = bytes,
            IsSaving = GetTask()?.State == TaskState.Running
        };
    }

    /// <summary>
    /// Turns trailer downloads on or off (saved trailers keep playing); it applies without a restart.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SaveSettings([FromBody, Required] TrailerSettingsDto settings, CancellationToken cancellationToken)
    {
        await _configurationStore.SaveTrailersAsync(new TrailerOptions { Enabled = settings.Enabled }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Deletes every trailer PirateFin saved, to free up space (trailers added by hand stay).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The number deleted.</response>
    /// <returns>An <see cref="OkResult"/> containing the number of trailers deleted.</returns>
    [HttpPost("DeleteAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> DeleteAll(CancellationToken cancellationToken)
        => await _trailers.DeleteAllAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Starts saving the missing trailers now (the "Save Trailers" scheduled task).
    /// </summary>
    /// <response code="204">Started.</response>
    /// <response code="404">The task isn't registered.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("SaveMissing")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SaveMissing()
    {
        var task = GetTask();
        if (task is null)
        {
            return NotFound();
        }

        if (task.State != TaskState.Running)
        {
            _taskManager.Execute(task, new TaskOptions());
        }

        return NoContent();
    }

    private IScheduledTaskWorker? GetTask()
        => _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == SaveTrailersTaskKey);
}
