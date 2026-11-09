using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// The Popular rows: once a day, the 50 most popular recent titles of each library, added to the library as
/// placeholders. Shows: TMDB popularity of shows airing in the last 6 months (new shows and new seasons), kept to the
/// ones that pass the Providers page's show rules (looked up on Trakt). Anime: AniList popularity of anime that started
/// in the last 6 months. Movies: TMDB popularity of movies released in the last year that YTS has. Saved in
/// &lt;data&gt;/piratefin/popular.json.
/// </summary>
public sealed class PopularService : BackgroundService, IPopularTitles
{
    /// <summary>
    /// How many titles each Popular row goes through (5 sets of 10).
    /// </summary>
    public const int MaxTitles = 50;

    private const int MaxTmdbPages = 10;

    private static readonly TimeSpan _refreshEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan _startDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _checkEvery = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly string[] _libraries = [PirateFinLibraries.Shows, PirateFinLibraries.Anime, PirateFinLibraries.Movies];

    private readonly TmdbPopularClient _tmdb;
    private readonly TraktClient _trakt;
    private readonly AniListClient _aniList;
    private readonly IInternetSearchService _searchService;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<PopularService> _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, PopularList>? _lists;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopularService"/> class.
    /// </summary>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="trakt">The Trakt client.</param>
    /// <param name="aniList">The AniList client.</param>
    /// <param name="searchService">Adds the titles to the library.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public PopularService(
        TmdbPopularClient tmdb,
        TraktClient trakt,
        AniListClient aniList,
        IInternetSearchService searchService,
        IApplicationPaths appPaths,
        ILogger<PopularService> logger)
    {
        _tmdb = tmdb;
        _trakt = trakt;
        _aniList = aniList;
        _searchService = searchService;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "popular.json");

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetTitles(string library)
    {
        lock (_lock)
        {
            return Load().TryGetValue(library, out var list) ? list.ItemIds.ToList() : [];
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the server finish starting up first.
        await Task.Delay(_startDelay, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var library in _libraries)
            {
                if (!IsStale(library))
                {
                    continue;
                }

                try
                {
                    await RefreshAsync(library, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Refreshing the Popular {Library} failed", library);
                }
            }

            await Task.Delay(_checkEvery, stoppingToken).ConfigureAwait(false);
        }
    }

    private bool IsStale(string library)
    {
        lock (_lock)
        {
            return !Load().TryGetValue(library, out var list) || DateTime.UtcNow - list.UpdatedUtc >= _refreshEvery;
        }
    }

    private async Task RefreshAsync(string library, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var ids = library switch
        {
            PirateFinLibraries.Shows => await GetShowsAsync(now, cancellationToken).ConfigureAwait(false),
            PirateFinLibraries.Anime => await GetAnimeAsync(now, cancellationToken).ConfigureAwait(false),
            _ => await GetMoviesAsync(now, cancellationToken).ConfigureAwait(false)
        };

        lock (_lock)
        {
            Load()[library] = new PopularList { UpdatedUtc = now, ItemIds = ids };
            Save();
        }

        _logger.LogInformation("Popular {Library}: {Count} titles", library, ids.Count);
    }

    private async Task<List<Guid>> GetShowsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        for (var page = 1; page <= MaxTmdbPages && ids.Count < MaxTitles; page++)
        {
            var tmdbIds = await _tmdb.GetPopularShowsAsync(now.AddMonths(-6), now, page, cancellationToken).ConfigureAwait(false);
            if (tmdbIds.Count == 0)
            {
                break;
            }

            foreach (var tmdbId in tmdbIds)
            {
                if (ids.Count >= MaxTitles)
                {
                    break;
                }

                await AddAsync(ids, $"show {tmdbId}", async () =>
                    await _trakt.GetAllowedShowByTmdbIdAsync(tmdbId, cancellationToken).ConfigureAwait(false) is { } show
                        ? await _searchService.AddShowAsync(show, cancellationToken).ConfigureAwait(false)
                        : null).ConfigureAwait(false);
            }
        }

        return ids;
    }

    private async Task<List<Guid>> GetAnimeAsync(DateTime now, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        foreach (var (id, title, romaji) in await _aniList.GetPopularAsync(now.AddMonths(-6), MaxTitles, cancellationToken).ConfigureAwait(false))
        {
            await AddAsync(ids, title, () => _searchService.AddAnimeAsync(id, title, romaji, cancellationToken)).ConfigureAwait(false);
        }

        return ids;
    }

    private async Task<List<Guid>> GetMoviesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        for (var page = 1; page <= MaxTmdbPages && ids.Count < MaxTitles; page++)
        {
            var tmdbIds = await _tmdb.GetPopularMoviesAsync(now.AddYears(-1), now, page, cancellationToken).ConfigureAwait(false);
            if (tmdbIds.Count == 0)
            {
                break;
            }

            foreach (var tmdbId in tmdbIds)
            {
                if (ids.Count >= MaxTitles)
                {
                    break;
                }

                await AddAsync(ids, $"movie {tmdbId}", async () =>
                    await _tmdb.GetMovieImdbIdAsync(tmdbId, cancellationToken).ConfigureAwait(false) is { } imdbId
                        ? await _searchService.AddMovieAsync(imdbId, cancellationToken).ConfigureAwait(false)
                        : null).ConfigureAwait(false);
            }
        }

        return ids;
    }

    // Adds one title to the library and the list (once: later seasons of the same anime are the same series).
    private async Task AddAsync(List<Guid> ids, string name, Func<Task<Guid?>> add)
    {
        try
        {
            if (await add().ConfigureAwait(false) is { } id && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not add popular {Name}", name);
        }
    }

    private Dictionary<string, PopularList> Load()
    {
        if (_lists is not null)
        {
            return _lists;
        }

        try
        {
            _lists = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, PopularList>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; the Popular rows are fetched again", FilePath);
            _lists = [];
        }

        return _lists;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
            File.Move(temp, FilePath, true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save the Popular rows");
        }
    }

    private sealed class PopularList
    {
        public DateTime UpdatedUtc { get; set; }

        public List<Guid> ItemIds { get; set; } = [];
    }
}
