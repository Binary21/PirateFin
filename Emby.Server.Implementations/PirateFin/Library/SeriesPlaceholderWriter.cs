using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Storage;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Creates or updates a placeholder series / season / episode tree. Shared by the weekly schedule and search.
/// Never waits for metadata: new items are handed to the <see cref="IMetadataRefreshQueue"/>.
/// </summary>
public sealed class SeriesPlaceholderWriter
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMetadataRefreshQueue _refreshQueue;
    private readonly LibraryStorage _storage;
    private readonly ILogger<SeriesPlaceholderWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesPlaceholderWriter"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="refreshQueue">The metadata refresh queue.</param>
    /// <param name="storage">Picks the library folder (drive) new series go to.</param>
    /// <param name="logger">The logger.</param>
    public SeriesPlaceholderWriter(ILibraryManager libraryManager, IMetadataRefreshQueue refreshQueue, LibraryStorage storage, ILogger<SeriesPlaceholderWriter> logger)
    {
        _libraryManager = libraryManager;
        _refreshQueue = refreshQueue;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Finds the series with this title in any of the library's folders (a library can span drives).
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="title">The series title.</param>
    /// <returns>The series, or <c>null</c>.</returns>
    public Series? FindSeries(PirateFinLibrary library, string title)
    {
        var name = PirateFinLibrary.SanitizeFileName(title);
        return library.Locations
            .Select(location => _libraryManager.GetItemById(_libraryManager.GetNewItemId(Path.Combine(location.Path, name), typeof(Series))))
            .OfType<Series>()
            .FirstOrDefault(series => !series.ParentId.IsEmpty());
    }

    /// <summary>
    /// Creates or updates the tree.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="seriesSpec">The series.</param>
    /// <param name="seasons">The seasons with the episodes to create / update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="into">An existing series to add the missing seasons / episodes to (e.g. one the user added), instead
    /// of the series PirateFin keeps at the title's folder.</param>
    /// <param name="first">Whether the new items' metadata is fetched ahead of everything else waiting.</param>
    /// <returns>The created or already existing series.</returns>
    public async Task<Series> WriteAsync(
        PirateFinLibrary library,
        SeriesSpec seriesSpec,
        IEnumerable<(SeasonSpec Season, IEnumerable<EpisodeSpec> Episodes)> seasons,
        CancellationToken cancellationToken,
        Series? into = null,
        bool first = false)
    {
        var (series, seriesCreated) = into is null ? GetOrCreateSeries(library, seriesSpec) : (into, false);
        var toRefresh = new List<BaseItem>();

        foreach (var (seasonSpec, episodes) in seasons)
        {
            var (season, seasonCreated) = GetOrCreateSeason(series, seasonSpec);

            // A new series' refresh already covers its season folders.
            if (seasonCreated && !seriesCreated)
            {
                toRefresh.Add(season);
            }

            // Episode numbers the season already has, e.g. files the user added under their own names. Only looked up
            // when an episode isn't found by its placeholder path.
            var existingNumbers = new Lazy<HashSet<int>>(() => seasonCreated ? [] : GetEpisodeNumbers(season));

            // New episodes are saved in one batch per season; long anime have hundreds.
            var newEpisodes = new List<BaseItem>();
            var seen = new HashSet<Guid>();
            foreach (var episodeSpec in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var created = await BuildOrUpdateEpisodeAsync(library, series, season, episodeSpec, existingNumbers, cancellationToken).ConfigureAwait(false);
                if (created is not null && seen.Add(created.Id))
                {
                    newEpisodes.Add(created);
                }
            }

            if (newEpisodes.Count > 0)
            {
                _libraryManager.CreateItems(newEpisodes, season, cancellationToken);
                toRefresh.AddRange(newEpisodes);
            }
        }

        if (seriesCreated || toRefresh.Count > 0)
        {
            if (first)
            {
                _refreshQueue.EnqueueFirst(seriesCreated ? series : null, toRefresh);
            }
            else
            {
                _refreshQueue.Enqueue(seriesCreated ? series : null, toRefresh);
            }
        }

        return series;
    }

    private (Series Series, bool Created) GetOrCreateSeries(PirateFinLibrary library, SeriesSpec spec)
    {
        if (FindSeries(library, spec.Title) is { } existing)
        {
            return (existing, false);
        }

        // New series go to the library folder whose drive has the most room.
        var location = _storage.ChooseNewItemLocation(library);
        var name = PirateFinLibrary.SanitizeFileName(spec.Title);
        var path = Path.Combine(location.Path, name);
        var id = _libraryManager.GetNewItemId(path, typeof(Series));

        _logger.LogInformation("Creating series {Name} in {Path}", name, path);
        Directory.CreateDirectory(path);

        var series = new Series
        {
            Id = id,
            Name = name,
            Path = path,
            IsInternet = true,
            ParentId = location.Folder.Id,
            Genres = spec.Genres,
            Overview = spec.Overview,
            ProductionYear = spec.Year,
            BackupImageUrl = spec.ImageUrl
        };
        if (!string.IsNullOrEmpty(spec.AniListId))
        {
            series.ProviderIds["AniList"] = spec.AniListId;
        }

        _libraryManager.CreateItem(series, location.Folder);
        return (series, true);
    }

    private (Season Season, bool Created) GetOrCreateSeason(Series series, SeasonSpec spec)
    {
        // A season the user added may be named differently ("Season 01", "S1"); it's the same season by number.
        var byNumber = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = [BaseItemKind.Season],
                IndexNumber = spec.Number
            })
            .OfType<Season>()
            .FirstOrDefault();
        if (byNumber is not null)
        {
            return (byNumber, false);
        }

        var path = Path.Combine(series.Path, spec.Title);
        var id = _libraryManager.GetNewItemId(path, typeof(Season));
        if (_libraryManager.GetItemById(id) is Season existing && !existing.ParentId.IsEmpty())
        {
            return (existing, false);
        }

        _logger.LogInformation("Creating {Season} for {Series}", spec.Title, series.Name);

        // The folder must exist or the next scan of the series would remove the season.
        Directory.CreateDirectory(path);

        var season = new Season
        {
            Id = id,
            Name = spec.Title,
            IndexNumber = spec.Number,
            Path = path,
            SeriesId = series.Id,
            SeriesName = series.Name,
            ParentId = series.Id,
            Scheduled = true,
            IsInternet = true,
            Subscribed = spec.Subscribed,
            Genres = spec.Genres,
            Overview = spec.Overview
        };
        _libraryManager.CreateItem(season, series);
        return (season, true);
    }

    // No title yet: empty, or the "Episode 5" a placeholder gets when the provider has none.
    private static bool HasNumberedName(Episode episode)
        => string.IsNullOrWhiteSpace(episode.Name)
            || (episode.IndexNumber is { } number
                && string.Equals(episode.Name, "Episode " + number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));

    private static HashSet<int> GetEpisodeNumbers(Season season)
    {
        var numbers = new HashSet<int>();
        foreach (var episode in season.GetEpisodes().OfType<Episode>())
        {
            if (episode.IndexNumber is { } first)
            {
                for (var number = first; number <= (episode.IndexNumberEnd ?? first); number++)
                {
                    numbers.Add(number);
                }
            }
        }

        return numbers;
    }

    // Returns a new (not yet saved) episode, or null when it already existed and was updated in place.
    private async Task<Episode?> BuildOrUpdateEpisodeAsync(
        PirateFinLibrary library,
        Series series,
        Season season,
        EpisodeSpec spec,
        Lazy<HashSet<int>> existingNumbers,
        CancellationToken cancellationToken)
    {
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{series.Name} - S{season.IndexNumber ?? 0:00}E{spec.Number:00}.mkv");

        // Seasons of a series without season folders have no path of their own.
        var path = Path.Combine(string.IsNullOrEmpty(season.Path) ? series.Path : season.Path, fileName);
        var id = _libraryManager.GetNewItemId(path, typeof(Episode));

        if (_libraryManager.GetItemById(id) is Episode existing)
        {
            await UpdateEpisodeAsync(existing, season, spec, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (existingNumbers.Value.Contains(spec.Number))
        {
            return null;
        }

        var episode = new Episode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(spec.Title)
                ? "Episode " + spec.Number.ToString(CultureInfo.InvariantCulture)
                : spec.Title,
            OriginalTitle = spec.Title,
            AlternateTitle = PirateFinLibrary.SanitizeFileName(spec.AlternateTitle),
            Overview = spec.Overview,
            FirstAiring = spec.FirstAiring,
            PremiereDate = spec.FirstAiring,
            Path = path,
            BackupImageUrl = spec.ImageUrl,
            DownloadUrl = spec.MagnetUrl,
            IsDownloadable = !string.IsNullOrEmpty(spec.MagnetUrl),
            IndexNumber = spec.Number,
            ParentIndexNumber = season.IndexNumber,
            SeriesId = series.Id,
            SeriesName = series.Name,
            SeasonId = season.Id,
            SeasonName = season.Name,
            ParentId = season.Id,
            LibraryType = library.Name,
            Genres = season.Genres,
            IsInternet = true,
            Scheduled = true,
            IsPlaceHolder = true,
            IsDownloaded = false,

            // No file exists yet; stays virtual until the download manager has real media on disk so ffprobe / image extraction skip it.
            IsVirtualItem = true
        };
        return episode;
    }

    // Keeps provider data current on placeholders (e.g. corrected air times), and fills in what an episode is missing
    // (a title instead of "Episode 5", the description, its own image) without replacing anything it already has.
    private async Task UpdateEpisodeAsync(Episode episode, Season season, EpisodeSpec spec, CancellationToken cancellationToken)
    {
        var changed = false;
        if (episode.IsInternet && !episode.IsDownloaded)
        {
            if (spec.FirstAiring.HasValue && episode.FirstAiring != spec.FirstAiring)
            {
                episode.FirstAiring = spec.FirstAiring;
                episode.PremiereDate = spec.FirstAiring;
                changed = true;
            }

            if (!string.IsNullOrEmpty(spec.MagnetUrl) && string.IsNullOrEmpty(episode.DownloadUrl))
            {
                episode.DownloadUrl = spec.MagnetUrl;
                episode.IsDownloadable = true;
                changed = true;
            }

            if (!string.IsNullOrEmpty(spec.StillUrl) && episode.BackupImageUrl != spec.StillUrl)
            {
                episode.BackupImageUrl = spec.StillUrl;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.Title) && HasNumberedName(episode) && episode.Name != spec.Title)
        {
            episode.Name = spec.Title;
            episode.OriginalTitle = spec.Title;
            changed = true;
        }

        if (string.IsNullOrEmpty(episode.Overview) && !string.IsNullOrEmpty(spec.Overview))
        {
            episode.Overview = spec.Overview;
            changed = true;
        }

        if (changed)
        {
            await _libraryManager.UpdateItemAsync(episode, season, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }
}
