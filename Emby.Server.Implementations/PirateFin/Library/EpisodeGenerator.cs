using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Creates placeholders for the weekly schedule.
/// </summary>
public sealed class EpisodeGenerator : IEpisodeGenerator
{
    private readonly ILibraryManager _libraryManager;
    private readonly SeriesPlaceholderWriter _writer;
    private readonly ILogger<EpisodeGenerator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeGenerator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="writer">The placeholder writer.</param>
    /// <param name="logger">The logger.</param>
    public EpisodeGenerator(ILibraryManager libraryManager, SeriesPlaceholderWriter writer, ILogger<EpisodeGenerator> logger)
    {
        _libraryManager = libraryManager;
        _writer = writer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task GenerateEpisodesAsync(IReadOnlyList<ScheduleEpisode> scheduleEpisodes, string library, CancellationToken cancellationToken)
    {
        if (scheduleEpisodes.Count == 0)
        {
            return;
        }

        var target = PirateFinLibrary.Resolve(_libraryManager, library, _logger);
        if (target is null)
        {
            return;
        }

        // AniList reports brand new shows as NOT_YET_RELEASED until their first episode airs.
        var bySeries = scheduleEpisodes
            .Where(e => e.AiringStatus != "NOT_YET_RELEASED" && !string.IsNullOrWhiteSpace(e.Title))
            .GroupBy(e => PirateFinLibrary.SanitizeFileName(e.Title), StringComparer.OrdinalIgnoreCase);

        foreach (var group in bySeries)
        {
            var first = group.First();
            var seriesSpec = new SeriesSpec(first.Title, string.Empty, first.ImageUrl, null, first.Genres, string.Empty);
            var seasons = group
                .GroupBy(e => e.Season)
                .Select(season => (
                    new SeasonSpec(season.Key, string.Empty, first.Genres, season.Any(e => e.Subscribed)),
                    season.Select(e => new EpisodeSpec(
                        e.Number ?? 0,
                        e.EpisodeTitle,
                        e.Overview,
                        e.FirstAiring,
                        e.AlternateTitle,
                        e.ImageUrl,
                        e.MagnetUrl))));

            try
            {
                await _writer.WriteAsync(target, seriesSpec, seasons, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to create placeholders for {Series}", first.Title);
            }
        }
    }
}
