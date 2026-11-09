using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// Searches Trakt / AniList / YTS without touching the library; a result becomes a library item only when opened.
/// </summary>
public sealed class InternetSearchService : IInternetSearchService
{
    private const int MaxShowResults = 10;
    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (DateTime Created, IReadOnlyList<SearchedSeries> Results)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Created, IReadOnlyList<ScheduleMovie> Results)> _movieCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly TraktClient _trakt;
    private readonly AniListClient _aniList;
    private readonly AnimeEpisodeDetails _animeDetails;
    private readonly YtsClient _yts;
    private readonly ISeriesGenerator _seriesGenerator;
    private readonly IMovieGenerator _movieGenerator;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<InternetSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InternetSearchService"/> class.
    /// </summary>
    /// <param name="trakt">The Trakt client.</param>
    /// <param name="animeDetails">Fills in anime episode titles, descriptions and stills from TMDB.</param>
    /// <param name="aniList">The AniList client.</param>
    /// <param name="yts">The YTS client.</param>
    /// <param name="seriesGenerator">The series generator.</param>
    /// <param name="movieGenerator">The movie generator.</param>
    /// <param name="subscriptionService">The subscription service.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public InternetSearchService(
        TraktClient trakt,
        AniListClient aniList,
        AnimeEpisodeDetails animeDetails,
        YtsClient yts,
        ISeriesGenerator seriesGenerator,
        IMovieGenerator movieGenerator,
        ISubscriptionService subscriptionService,
        ILibraryManager libraryManager,
        ILogger<InternetSearchService> logger)
    {
        _trakt = trakt;
        _aniList = aniList;
        _animeDetails = animeDetails;
        _yts = yts;
        _seriesGenerator = seriesGenerator;
        _movieGenerator = movieGenerator;
        _subscriptionService = subscriptionService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InternetSearchResult>> SearchAsync(string library, string query, CancellationToken cancellationToken)
    {
        if (library == PirateFinLibraries.Movies)
        {
            var movies = await GetMovieResultsAsync(query, cancellationToken).ConfigureAwait(false);
            return movies.Select(m => new InternetSearchResult
                {
                    Key = GetKey(m),
                    Library = library,
                    Title = m.Title,
                    Year = m.Year > 0 ? m.Year : null,
                    Overview = m.Overview,
                    ImageUrl = m.ImageUrl,
                    Genres = m.Genres,
                    ItemId = _movieGenerator.FindExistingMovieId(m, library)
                })
                .ToList();
        }

        var series = await GetResultsAsync(library, query, cancellationToken).ConfigureAwait(false);
        return series.Select(s => new InternetSearchResult
            {
                Key = GetKey(library, s),
                Library = library,
                Title = s.Title,
                Year = s.Year,
                Overview = s.Overview,
                ImageUrl = s.ImageUrl,
                Genres = s.Genres,
                ItemId = _seriesGenerator.FindExistingSeriesId(s.Title, library)
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<Guid?> OpenAsync(string library, string key, string query, CancellationToken cancellationToken)
    {
        if (library == PirateFinLibraries.Movies)
        {
            var movies = await GetMovieResultsAsync(query, cancellationToken).ConfigureAwait(false);
            var movie = movies.FirstOrDefault(m => string.Equals(GetKey(m), key, StringComparison.OrdinalIgnoreCase));
            if (movie is null)
            {
                _logger.LogWarning("Movie search result {Key} for '{Query}' no longer exists", key, query);
                return null;
            }

            await _movieGenerator.GenerateMoviesAsync([movie], library, cancellationToken).ConfigureAwait(false);
            return _movieGenerator.FindExistingMovieId(movie, library);
        }

        var results = await GetResultsAsync(library, query, cancellationToken).ConfigureAwait(false);
        var match = results.FirstOrDefault(s => string.Equals(GetKey(library, s), key, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _logger.LogWarning("Search result {Key} for '{Query}' no longer exists", key, query);
            return null;
        }

        var generated = await GenerateAsync(library, match, cancellationToken).ConfigureAwait(false);
        return generated?.Series.Id;
    }

    /// <inheritdoc />
    public Task<(int Seasons, int Episodes)?> RefreshSeriesAsync(Series series, CancellationToken cancellationToken)
        => RefreshAsync(series, null, cancellationToken);

    /// <inheritdoc />
    public Task<(int Seasons, int Episodes)?> RefreshSeasonFirstAsync(Series series, int seasonNumber, CancellationToken cancellationToken)
        => RefreshAsync(series, seasonNumber, cancellationToken);

    /// <inheritdoc />
    public async Task<Guid?> AddShowAsync(SearchedSeries show, CancellationToken cancellationToken)
    {
        var generated = await GenerateAsync(PirateFinLibraries.Shows, show, cancellationToken).ConfigureAwait(false);
        return generated?.Series.Id;
    }

    /// <inheritdoc />
    public async Task<Guid?> AddAnimeAsync(string aniListId, string title, string romajiTitle, CancellationToken cancellationToken)
    {
        // The entry may be a later season: find the series (prequel / sequel chain) it is part of.
        var results = await GetResultsAsync(PirateFinLibraries.Anime, title, cancellationToken).ConfigureAwait(false);
        var match = results.FirstOrDefault(r => r.AniListId == aniListId)
            ?? results.FirstOrDefault(r => r.Seasons.Any(s => string.Equals(s.RomajiTitle, romajiTitle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.AlternateTitle, title, StringComparison.OrdinalIgnoreCase)));
        if (match is null)
        {
            _logger.LogInformation("Popular anime {Title} ({Id}) has no matching search result", title, aniListId);
            return null;
        }

        var generated = await GenerateAsync(PirateFinLibraries.Anime, match, cancellationToken).ConfigureAwait(false);
        return generated?.Series.Id;
    }

    /// <inheritdoc />
    public async Task<Guid?> AddMovieAsync(string imdbId, CancellationToken cancellationToken)
    {
        var movies = await _yts.SearchMoviesAsync(imdbId, cancellationToken).ConfigureAwait(false);
        var movie = movies.FirstOrDefault(m => string.Equals(m.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase));
        if (movie is null)
        {
            return null;
        }

        await _movieGenerator.GenerateMoviesAsync([movie], PirateFinLibraries.Movies, cancellationToken).ConfigureAwait(false);
        return _movieGenerator.FindExistingMovieId(movie, PirateFinLibraries.Movies);
    }

    // Finds the series on Trakt / AniList and adds what it's missing; with firstSeason that season comes first and the
    // rest follows in the background.
    private async Task<(int Seasons, int Episodes)?> RefreshAsync(Series series, int? firstSeason, CancellationToken cancellationToken)
    {
        // Series outside the PirateFin libraries (added by the user) are looked up as shows.
        var library = _libraryManager.GetCollectionFolders(series)
            .Select(f => PirateFinLibraries.Normalize(f.Name))
            .FirstOrDefault(l => l is PirateFinLibraries.Shows or PirateFinLibraries.Anime)
            ?? PirateFinLibraries.Shows;

        var results = await GetResultsAsync(library, series.Name, cancellationToken, forRefresh: true).ConfigureAwait(false);
        var match = FindMatch(series, library, results);
        if (match is null)
        {
            _logger.LogInformation("Refreshing {Series}: no {Library} search result matches it", series.Name, library);
            return null;
        }

        var generated = await GenerateAsync(library, match, cancellationToken, series, firstSeason).ConfigureAwait(false);
        if (generated is not { } result)
        {
            return null;
        }

        // A series the user added becomes a PirateFin one once it has placeholders, so its seasons can be subscribed to.
        if (result.EpisodesAdded > 0)
        {
            await MarkInternetAsync(series, cancellationToken).ConfigureAwait(false);
        }

        return (result.SeasonsAdded, result.EpisodesAdded);
    }

    private static string NormalizeTitle(string title)
        => new string(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // The search result a library series came from: by AniList id, by the title PirateFin derived its id from, or (for
    // series the user added) by title, preferring the same year.
    private SearchedSeries? FindMatch(Series series, string library, IReadOnlyList<SearchedSeries> results)
    {
        series.ProviderIds.TryGetValue("AniList", out var aniListId);
        var title = NormalizeTitle(series.Name);
        var sameTitle = results.Where(r => NormalizeTitle(r.Title) == title).ToList();
        return results.FirstOrDefault(r => !string.IsNullOrEmpty(aniListId) && r.AniListId == aniListId)
            ?? results.FirstOrDefault(r => _seriesGenerator.FindExistingSeriesId(r.Title, library) is { } id && id.Equals(series.Id))
            ?? sameTitle.FirstOrDefault(r => series.ProductionYear.HasValue && r.Year == series.ProductionYear)
            ?? sameTitle.FirstOrDefault();
    }

    // Marks the series, and its seasons that now hold placeholders, as PirateFin items.
    private async Task MarkInternetAsync(Series series, CancellationToken cancellationToken)
    {
        if (!series.IsInternet)
        {
            _logger.LogInformation("{Series} now has placeholders; it is a PirateFin series from now on", series.Name);
            series.IsInternet = true;
            await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = [BaseItemKind.Season],
                IsInternet = false
            })
            .OfType<Season>();
        foreach (var season in seasons)
        {
            if (season.GetEpisodes().Any(e => e.IsInternet))
            {
                season.IsInternet = true;
                await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Creates the series, or adds the seasons / episodes it's missing (existing ones are only updated), oldest season
    // first and specials last. With into, the missing ones are added to that existing series. With firstSeason (of a
    // series already in the library) only that season is added before returning; the others follow in the background.
    private async Task<(Series Series, int SeasonsAdded, int EpisodesAdded)?> GenerateAsync(
        string library,
        SearchedSeries match,
        CancellationToken cancellationToken,
        Series? into = null,
        int? firstSeason = null)
    {
        if (library == PirateFinLibraries.Shows && match.Seasons.Count == 0)
        {
            match.Seasons = await _trakt.GetSeasonsAsync(match.AlternateTitle, match.Genres, cancellationToken).ConfigureAwait(false);
        }

        var existingId = into?.Id ?? _seriesGenerator.FindExistingSeriesId(match.Title, library);
        var ordered = SeriesGenerator.Chronological(match.Seasons).ToList();
        var first = firstSeason.HasValue && existingId.HasValue ? ordered.FirstOrDefault(s => s.Season == firstSeason.Value) : null;
        if (first is null)
        {
            return await GeneratePartAsync(library, SeriesGenerator.WithSeasons(match, ordered), existingId, into, false, cancellationToken).ConfigureAwait(false);
        }

        var result = await GeneratePartAsync(library, SeriesGenerator.WithSeasons(match, [first]), existingId, into, true, cancellationToken).ConfigureAwait(false);
        var rest = ordered.Where(s => !ReferenceEquals(s, first)).ToList();
        if (result is { } done && rest.Count > 0)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        var more = await GeneratePartAsync(library, SeriesGenerator.WithSeasons(match, rest), done.Series.Id, into ?? done.Series, false, CancellationToken.None).ConfigureAwait(false);
                        if (more is { EpisodesAdded: > 0 } && into is not null)
                        {
                            await MarkInternetAsync(done.Series, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Adding the other seasons of {Series} failed", match.Title);
                    }
                },
                CancellationToken.None);
        }

        return result;
    }

    // Writes some seasons of a series (anime episodes get their details from TMDB first).
    private async Task<(Series Series, int SeasonsAdded, int EpisodesAdded)?> GeneratePartAsync(
        string library,
        SearchedSeries part,
        Guid? existingId,
        Series? into,
        bool first,
        CancellationToken cancellationToken)
    {
        if (library == PirateFinLibraries.Anime)
        {
            await _aniList.CompleteScheduleAsync(part, cancellationToken).ConfigureAwait(false);
            await _animeDetails.FillInAsync(part, cancellationToken).ConfigureAwait(false);
        }

        var (seasonsBefore, episodesBefore) = existingId.HasValue ? CountChildren(existingId.Value) : (0, 0);

        var series = await _seriesGenerator.GenerateSeriesAsync(part, library, cancellationToken, into, first).ConfigureAwait(false);
        if (series is null)
        {
            return null;
        }

        var (seasonsAfter, episodesAfter) = CountChildren(series.Id);
        var seasonsAdded = seasonsAfter - seasonsBefore;
        var episodesAdded = episodesAfter - episodesBefore;
        if (existingId.HasValue)
        {
            _logger.LogInformation("{Series} was in the library; added {Seasons} seasons and {Episodes} episodes", series.Name, seasonsAdded, episodesAdded);
        }

        // Only new series / episodes need their whole-season releases looked up.
        if (library == PirateFinLibraries.Shows && (!existingId.HasValue || episodesAdded > 0))
        {
            // Look for whole-season author releases in the background; the user is already looking at the series.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await _subscriptionService.ProcessSeasonReleasesAsync(part, series, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Season release search failed for {Series}", part.Title);
                    }
                },
                CancellationToken.None);
        }

        return (series, seasonsAdded, episodesAdded);
    }

    private (int Seasons, int Episodes) CountChildren(Guid seriesId)
    {
        int Count(BaseItemKind kind) => _libraryManager.GetCount(new InternalItemsQuery
        {
            AncestorIds = [seriesId],
            IncludeItemTypes = [kind],
            Recursive = true
        });

        return (Count(BaseItemKind.Season), Count(BaseItemKind.Episode));
    }

    private static string GetKey(string library, SearchedSeries series)
        => library == PirateFinLibraries.Anime ? series.AniListId : series.AlternateTitle;

    private static string GetKey(ScheduleMovie movie)
        => string.IsNullOrEmpty(movie.ImdbId) ? movie.FileTitle : movie.ImdbId;

    private async Task<IReadOnlyList<ScheduleMovie>> GetMovieResultsAsync(string query, CancellationToken cancellationToken)
    {
        var cacheKey = query.Trim();
        var now = DateTime.UtcNow;
        if (_movieCache.TryGetValue(cacheKey, out var cached) && now - cached.Created < _cacheLifetime)
        {
            return cached.Results;
        }

        var results = await _yts.SearchMoviesAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        foreach (var expired in _movieCache.Where(c => now - c.Value.Created >= _cacheLifetime).Select(c => c.Key).ToList())
        {
            _movieCache.TryRemove(expired, out _);
        }

        _movieCache[cacheKey] = (now, results);
        return results;
    }

    // A refresh gets fresh results (cached ones keep the seasons they were opened with). For anime they include the
    // excluded countries, since the user added the series themselves, so they aren't cached for the normal search.
    private async Task<IReadOnlyList<SearchedSeries>> GetResultsAsync(string library, string query, CancellationToken cancellationToken, bool forRefresh = false)
    {
        var cacheKey = library + "|" + query.Trim();
        var now = DateTime.UtcNow;
        if (!forRefresh && _cache.TryGetValue(cacheKey, out var cached) && now - cached.Created < _cacheLifetime)
        {
            return cached.Results;
        }

        IReadOnlyList<SearchedSeries> results = library switch
        {
            PirateFinLibraries.Anime => await _aniList.SearchSeriesAsync(query, cancellationToken, allCountries: forRefresh).ConfigureAwait(false),
            PirateFinLibraries.Shows => await _trakt.SearchShowsAsync(query, MaxShowResults, cancellationToken).ConfigureAwait(false),
            _ => []
        };

        foreach (var expired in _cache.Where(c => now - c.Value.Created >= _cacheLifetime).Select(c => c.Key).ToList())
        {
            _cache.TryRemove(expired, out _);
        }

        if (!forRefresh || library != PirateFinLibraries.Anime)
        {
            _cache[cacheKey] = (now, results);
        }

        return results;
    }
}
