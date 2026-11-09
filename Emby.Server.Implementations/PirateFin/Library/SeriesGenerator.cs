using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Creates a full placeholder series from a search result.
/// </summary>
public sealed class SeriesGenerator : ISeriesGenerator
{
    private readonly ILibraryManager _libraryManager;
    private readonly SeriesPlaceholderWriter _writer;
    private readonly ILogger<SeriesGenerator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesGenerator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="writer">The placeholder writer.</param>
    /// <param name="logger">The logger.</param>
    public SeriesGenerator(ILibraryManager libraryManager, SeriesPlaceholderWriter writer, ILogger<SeriesGenerator> logger)
    {
        _libraryManager = libraryManager;
        _writer = writer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Series?> GenerateSeriesAsync(SearchedSeries searchedSeries, string library, CancellationToken cancellationToken, Series? into = null, bool first = false)
    {
        if (string.IsNullOrWhiteSpace(searchedSeries.Title))
        {
            return null;
        }

        var target = PirateFinLibrary.Resolve(_libraryManager, library, _logger);
        if (target is null)
        {
            return null;
        }

        var seriesSpec = new SeriesSpec(
            searchedSeries.Title,
            searchedSeries.Overview,
            searchedSeries.ImageUrl,
            searchedSeries.Year,
            searchedSeries.Genres,
            searchedSeries.AniListId);

        var seasons = searchedSeries.Seasons.Select(season => (
            new SeasonSpec(season.Season, season.Overview, season.Genres, false),
            season.Episodes
                .Where(e => e.Episode > 0)
                .Select(e => new EpisodeSpec(
                    e.Episode,
                    e.Title,
                    e.Overview,
                    e.FirstAiring,
                    season.RomajiTitle,
                    FirstNonEmpty(e.ImageUrl, season.ImageUrl, searchedSeries.ImageUrl),
                    string.Empty,
                    e.ImageUrl))));

        return await _writer.WriteAsync(target, seriesSpec, seasons, cancellationToken, into, first).ConfigureAwait(false);
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? string.Empty;

    /// <summary>
    /// Puts seasons in the order they're built: oldest first, specials last.
    /// </summary>
    /// <param name="seasons">The seasons.</param>
    /// <returns>The seasons in order.</returns>
    public static IEnumerable<SearchedSeason> Chronological(IEnumerable<SearchedSeason> seasons)
        => seasons.OrderBy(s => s.Season == 0 ? int.MaxValue : s.Season);

    /// <summary>
    /// A copy of a search result with only some of its seasons.
    /// </summary>
    /// <param name="series">The search result.</param>
    /// <param name="seasons">The seasons to keep.</param>
    /// <returns>The copy.</returns>
    public static SearchedSeries WithSeasons(SearchedSeries series, IEnumerable<SearchedSeason> seasons)
        => new()
        {
            Title = series.Title,
            AlternateTitle = series.AlternateTitle,
            Year = series.Year,
            Overview = series.Overview,
            ImageUrl = series.ImageUrl,
            Genres = series.Genres,
            AniListId = series.AniListId,
            Seasons = seasons.ToList()
        };

    /// <inheritdoc />
    public Guid? FindExistingSeriesId(string title, string library)
    {
        var target = PirateFinLibrary.Resolve(_libraryManager, library, _logger);
        if (target is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return _writer.FindSeries(target, title)?.Id;
    }
}
