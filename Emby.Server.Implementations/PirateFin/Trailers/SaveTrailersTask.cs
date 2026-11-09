using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Trailers;

/// <summary>
/// Saves the trailers of downloaded movies and series that haven't got one: everything downloaded before trailers were
/// saved, and anything missed (e.g. while its drive was full). Runs at startup and daily.
/// </summary>
public sealed class SaveTrailersTask : IScheduledTask
{
    /// <summary>
    /// The task's key.
    /// </summary>
    public const string TaskKey = "PirateFinSaveTrailers";

    private readonly ITrailerLibrary _trailers;
    private readonly IOptionsMonitor<TrailerOptions> _options;
    private readonly ILogger<SaveTrailersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveTrailersTask"/> class.
    /// </summary>
    /// <param name="trailers">The trailer library.</param>
    /// <param name="options">The trailer options.</param>
    /// <param name="logger">The logger.</param>
    public SaveTrailersTask(ITrailerLibrary trailers, IOptionsMonitor<TrailerOptions> options, ILogger<SaveTrailersTask> logger)
    {
        _trailers = trailers;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Save Trailers";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Saves the first 30 seconds of the YouTube trailer of each downloaded movie and series next to it, for the billboard and the Trailer button.";

    /// <inheritdoc />
    public string Category => "PirateFin";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        var missing = _trailers.GetMissing();
        var saved = 0;
        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_options.CurrentValue.Enabled)
            {
                break;
            }

            try
            {
                if (await _trailers.SaveAsync(missing[i], cancellationToken).ConfigureAwait(false) == TrailerSaveResult.Saved)
                {
                    saved++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to save the trailer of {Title}", missing[i].Name);
            }

            progress.Report(100.0 * (i + 1) / missing.Count);
        }

        _logger.LogInformation("Saved {Saved} of {Missing} missing trailers", saved, missing.Count);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks };
    }
}
