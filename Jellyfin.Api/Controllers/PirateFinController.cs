using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.ModelBinders;
using Jellyfin.Api.Models.PirateFinDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.PirateFin;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// PirateFin: internet search, newest releases, download sources and season subscriptions.
/// </summary>
[Route("PirateFin")]
[Authorize]
public class PirateFinController : BaseJellyfinApiController
{
    // The "airing today" rows are topped up to this many shows from the previous days.
    private const int NewestMinimum = 10;
    private const int NewestFallbackDays = 4;

    // How recent a followed episode must be to be billboarded as new.
    private const int NewEpisodeDays = 7;

    private static readonly string[] _adultGenres = ["Hentai"];
    private static readonly string[] _adultTags = ["Nudity"];

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly IFileSystem _fileSystem;
    private readonly IInternetSearchService _searchService;
    private readonly IInternetScheduleFetcher _scheduleFetcher;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPirateFinConfigurationStore _configurationStore;
    private readonly IPirateFinLibraryService _libraryService;
    private readonly IDownloadManager _downloadManager;
    private readonly IDownloadProgress _downloadProgress;
    private readonly ILibraryStorage _storage;
    private readonly IStorageRebalancer _rebalancer;
    private readonly IPlaybackHistory _history;
    private readonly ISeasonSubscriptions _subscriptions;
    private readonly IProfileSettings _profiles;
    private readonly IPopularTitles _popular;
    private readonly ITorrentSearchService _torrentSearch;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="searchService">Instance of the <see cref="IInternetSearchService"/> interface.</param>
    /// <param name="scheduleFetcher">Instance of the <see cref="IInternetScheduleFetcher"/> interface.</param>
    /// <param name="subscriptionService">Instance of the <see cref="ISubscriptionService"/> interface.</param>
    /// <param name="configurationStore">Instance of the <see cref="IPirateFinConfigurationStore"/> interface.</param>
    /// <param name="libraryService">Instance of the <see cref="IPirateFinLibraryService"/> interface.</param>
    /// <param name="downloadManager">Instance of the <see cref="IDownloadManager"/> interface.</param>
    /// <param name="downloadProgress">Instance of the <see cref="IDownloadProgress"/> interface.</param>
    /// <param name="storage">Instance of the <see cref="ILibraryStorage"/> interface.</param>
    /// <param name="rebalancer">Instance of the <see cref="IStorageRebalancer"/> interface.</param>
    /// <param name="history">Instance of the <see cref="IPlaybackHistory"/> interface.</param>
    /// <param name="subscriptions">Instance of the <see cref="ISeasonSubscriptions"/> interface.</param>
    /// <param name="profiles">Instance of the <see cref="IProfileSettings"/> interface.</param>
    /// <param name="popular">Instance of the <see cref="IPopularTitles"/> interface.</param>
    /// <param name="torrentSearch">Instance of the <see cref="ITorrentSearchService"/> interface.</param>
    public PirateFinController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        IFileSystem fileSystem,
        IInternetSearchService searchService,
        IInternetScheduleFetcher scheduleFetcher,
        ISubscriptionService subscriptionService,
        IPirateFinConfigurationStore configurationStore,
        IPirateFinLibraryService libraryService,
        IDownloadManager downloadManager,
        IDownloadProgress downloadProgress,
        ILibraryStorage storage,
        IStorageRebalancer rebalancer,
        IPlaybackHistory history,
        ISeasonSubscriptions subscriptions,
        IProfileSettings profiles,
        IPopularTitles popular,
        ITorrentSearchService torrentSearch)
    {
        _torrentSearch = torrentSearch;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _fileSystem = fileSystem;
        _searchService = searchService;
        _scheduleFetcher = scheduleFetcher;
        _subscriptionService = subscriptionService;
        _configurationStore = configurationStore;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _downloadProgress = downloadProgress;
        _storage = storage;
        _rebalancer = rebalancer;
        _history = history;
        _subscriptions = subscriptions;
        _profiles = profiles;
        _popular = popular;
    }

    /// <summary>
    /// Gets a set of a library's Popular row: the most popular recent titles (refreshed daily), 10 at a time. The sets
    /// loop: after the last comes the first again.
    /// </summary>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <param name="set">Which set of titles, from 0.</param>
    /// <param name="setSize">How many titles a set has.</param>
    /// <param name="fields">Optional. Additional fields to return.</param>
    /// <response code="200">The titles, and how many sets there are.</response>
    /// <response code="400">Unknown library.</response>
    /// <returns>An <see cref="OkResult"/> containing the titles.</returns>
    [HttpGet("Popular")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<QueryResult<BaseItemDto>> GetPopular(
        [FromQuery, Required] string library,
        [FromQuery] int set = 0,
        [FromQuery] int setSize = 10,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[]? fields = null)
    {
        var normalized = PirateFinLibraries.Normalize(library);
        if (normalized is null)
        {
            return BadRequest("Library must be Shows, Anime or Movies.");
        }

        var user = GetUser(null);
        if (user is null)
        {
            return new QueryResult<BaseItemDto>();
        }

        // Only what this user may see (parental rating, library access).
        var titles = _popular.GetTitles(normalized)
            .Select(id => _libraryManager.GetItemById<BaseItem>(id, user))
            .OfType<BaseItem>()
            .ToList();
        setSize = Math.Clamp(setSize, 1, 50);
        var sets = (titles.Count + setSize - 1) / setSize;
        if (sets == 0)
        {
            return new QueryResult<BaseItemDto>();
        }

        var page = titles.Skip((((set % sets) + sets) % sets) * setSize).Take(setSize).ToList();
        var dtoOptions = new DtoOptions { Fields = fields ?? [] };
        return new QueryResult<BaseItemDto>(0, sets, _dtoService.GetBaseItemDtos(page, dtoOptions, user));
    }

    /// <summary>
    /// Searches Trakt (Shows), AniList (Anime) or YTS (Movies). Nothing is added to the library.
    /// </summary>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The results.</response>
    /// <response code="400">Unknown library.</response>
    /// <returns>An <see cref="OkResult"/> containing the search results.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<InternetSearchResult>>> Search(
        [FromQuery, Required] string library,
        [FromQuery, Required] string query,
        CancellationToken cancellationToken)
    {
        var normalized = PirateFinLibraries.Normalize(library);
        if (normalized is null)
        {
            return BadRequest("Library must be Shows, Anime or Movies.");
        }

        // Kids profiles can't add titles to the library.
        if (string.IsNullOrWhiteSpace(query) || _profiles.IsKids(RequestHelpers.GetUserId(User, null)))
        {
            return Array.Empty<InternetSearchResult>();
        }

        var results = await _searchService.SearchAsync(normalized, query.Trim(), cancellationToken).ConfigureAwait(false);
        _history.RecordSearch(RequestHelpers.GetUserId(User, null), normalized, query, results.Count);
        return Ok(results);
    }

    /// <summary>
    /// Adds a search result to the library (a movie, or a series with its seasons and placeholder episodes). Metadata is fetched in the background.
    /// </summary>
    /// <param name="request">The search result.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The series id.</response>
    /// <response code="400">Unknown library.</response>
    /// <response code="404">The result could not be found or the library is not configured.</response>
    /// <returns>An <see cref="OkResult"/> containing the id of the created series.</returns>
    [HttpPost("Search/Open")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OpenedSeriesDto>> OpenSearchResult(
        [FromBody, Required] OpenSearchResultDto request,
        CancellationToken cancellationToken)
    {
        if (_profiles.IsKids(RequestHelpers.GetUserId(User, null)))
        {
            return Forbid();
        }

        var library = PirateFinLibraries.Normalize(request.Library);
        if (library is null)
        {
            return BadRequest("Library must be Shows, Anime or Movies.");
        }

        var itemId = await _searchService.OpenAsync(library, request.Key, request.Query.Trim(), cancellationToken).ConfigureAwait(false);
        return itemId.HasValue ? new OpenedSeriesDto { ItemId = itemId.Value } : NotFound();
    }

    /// <summary>
    /// Searches Trakt / AniList for a series (or a season's series) by its title and adds the seasons and episodes the
    /// library is missing as placeholders. Existing seasons and episodes are kept; a series the user added is marked as a
    /// PirateFin series once it gets placeholders, so its seasons can be subscribed to. For a season, that season is
    /// added (and its metadata fetched) first and the result is about it; the other seasons follow in the background.
    /// </summary>
    /// <param name="itemId">The series or season id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">What was added.</response>
    /// <response code="404">Not a series / season, or the series wasn't found on Trakt / AniList.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="RefreshSeriesResultDto"/>.</returns>
    [HttpPost("Items/{itemId}/Refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RefreshSeriesResultDto>> RefreshSeries([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        var item = GetUserItem(itemId);
        var series = item switch
        {
            Series s => s,
            Season season => season.Series,
            _ => null
        };
        if (series is null)
        {
            return NotFound();
        }

        // Series the user added are filled in too, and become PirateFin series once they get placeholders. From a
        // season, that season is done first and the others follow in the background.
        var added = item is Season { IndexNumber: { } seasonNumber }
            ? await _searchService.RefreshSeasonFirstAsync(series, seasonNumber, cancellationToken).ConfigureAwait(false)
            : await _searchService.RefreshSeriesAsync(series, cancellationToken).ConfigureAwait(false);
        if (added is not var (seasons, episodes))
        {
            return NotFound($"{series.Name} wasn't found on Trakt / AniList.");
        }

        return new RefreshSeriesResultDto { SeasonsAdded = seasons, EpisodesAdded = episodes };
    }

    /// <summary>
    /// Gets the newest internet items of a library: for Shows / Anime the episodes airing on the viewer's current day
    /// (one per series), topped up on quiet days with the best rated downloadable shows of the previous days; for Movies
    /// the most recently released placeholders.
    /// </summary>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <param name="userId">Optional. The user id.</param>
    /// <param name="utcOffsetMinutes">The viewer's offset from UTC in minutes (e.g. -240 for EDT), defining "today".</param>
    /// <param name="limit">The maximum number of items.</param>
    /// <param name="fields">Optional. Additional fields to return.</param>
    /// <response code="200">The items.</response>
    /// <response code="400">Unknown library.</response>
    /// <response code="404">The library does not exist.</response>
    /// <returns>An <see cref="OkResult"/> containing the newest items.</returns>
    [HttpGet("Newest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<BaseItemDto>> GetNewest(
        [FromQuery, Required] string library,
        [FromQuery] Guid? userId,
        [FromQuery] int utcOffsetMinutes = 0,
        [FromQuery] int limit = 20,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[]? fields = null)
    {
        var normalized = PirateFinLibraries.Normalize(library);
        if (normalized is null)
        {
            return BadRequest("Library must be Shows, Anime or Movies.");
        }

        var user = GetUser(userId);
        var collectionFolderId = GetCollectionFolderId(normalized);
        if (user is null || collectionFolderId is null)
        {
            return NotFound();
        }

        var query = new InternalItemsQuery(user)
        {
            ParentId = collectionFolderId.Value,
            Recursive = true,
            IsInternet = true,
            IncludeItemTypes = [normalized == PirateFinLibraries.Movies ? BaseItemKind.Movie : BaseItemKind.Episode]
        };

        IEnumerable<BaseItem> items;
        if (normalized == PirateFinLibraries.Movies)
        {
            items = _libraryManager.GetItemList(query)
                .OfType<Movie>()
                .OrderByDescending(m => m.FirstAiring ?? m.DateCreated);
        }
        else
        {
            var offset = TimeSpan.FromMinutes(Math.Clamp(utcOffsetMinutes, -14 * 60, 14 * 60));
            var localToday = (DateTime.UtcNow + offset).Date;
            var dayStartUtc = DateTime.SpecifyKind(localToday - offset, DateTimeKind.Utc);
            var dayEndUtc = dayStartUtc.AddDays(1);

            var episodes = _libraryManager.GetItemList(query)
                .OfType<Episode>()
                .Where(e => e.FirstAiring.HasValue && !IsAdult(e.Series))
                .ToList();

            // Several episodes of a show can air the same day; show the first one.
            var today = episodes
                .Where(e => ReleaseStatusResolver.ToUtc(e.FirstAiring!.Value) is var airs && airs >= dayStartUtc && airs < dayEndUtc)
                .GroupBy(e => e.SeriesId)
                .Select(g => g.OrderBy(e => e.FirstAiring).ThenBy(e => e.IndexNumber).First())
                .OrderBy(e => e.FirstAiring)
                .ToList();

            // Quiet days (few shows air on weekends): top up with the best rated shows of the last few days that can be
            // downloaded, their latest episode each.
            if (today.Count < NewestMinimum)
            {
                var shown = today.Select(e => e.SeriesId).ToHashSet();
                var earlierStartUtc = dayStartUtc.AddDays(-NewestFallbackDays);
                today.AddRange(episodes
                    .Where(e => !shown.Contains(e.SeriesId)
                        && (!string.IsNullOrEmpty(e.DownloadUrl) || e.IsDownloaded)
                        && ReleaseStatusResolver.ToUtc(e.FirstAiring!.Value) is var airs
                        && airs >= earlierStartUtc
                        && airs < dayStartUtc)
                    .GroupBy(e => e.SeriesId)
                    .Select(g => g.OrderByDescending(e => e.FirstAiring).ThenByDescending(e => e.IndexNumber).First())
                    .OrderByDescending(e => e.Series?.CommunityRating ?? 0)
                    .ThenByDescending(e => e.FirstAiring)
                    .Take(NewestMinimum - today.Count));
            }

            items = today;
        }

        var dtoOptions = new DtoOptions { Fields = fields ?? [] };
        return Ok(_dtoService.GetBaseItemDtos(items.Take(Math.Clamp(limit, 1, 200)).ToList(), dtoOptions, user));
    }

    /// <summary>
    /// Gets the newest episode of each series the user follows that aired in the last <see cref="NewEpisodeDays"/> days,
    /// can be played and hasn't been watched by them, newest first. The billboard shows these as "New Episode".
    /// </summary>
    /// <param name="library">Shows or Anime.</param>
    /// <param name="limit">The maximum number of episodes.</param>
    /// <param name="fields">Optional. Additional fields to return.</param>
    /// <response code="200">The episodes.</response>
    /// <response code="400">Unknown library.</response>
    /// <response code="404">The library does not exist.</response>
    /// <returns>An <see cref="OkResult"/> containing the episodes.</returns>
    [HttpGet("NewSubscribedEpisodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<BaseItemDto>> GetNewSubscribedEpisodes(
        [FromQuery, Required] string library,
        [FromQuery] int limit = 8,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[]? fields = null)
    {
        var normalized = PirateFinLibraries.Normalize(library);
        if (normalized is null)
        {
            return BadRequest("Library must be Shows, Anime or Movies.");
        }

        var user = GetUser(null);
        var collectionFolderId = GetCollectionFolderId(normalized);
        if (user is null || collectionFolderId is null)
        {
            return NotFound();
        }

        if (normalized == PirateFinLibraries.Movies)
        {
            return Array.Empty<BaseItemDto>();
        }

        var since = DateTime.UtcNow.AddDays(-NewEpisodeDays);
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ParentId = collectionFolderId.Value,
                Recursive = true,
                IsPlayed = false,
                IncludeItemTypes = [BaseItemKind.Episode]
            })
            .OfType<Episode>()
            .Where(e => (e.FirstAiring ?? e.PremiereDate) is { } aired
                && ReleaseStatusResolver.ToUtc(aired) is var airedUtc
                && airedUtc >= since
                && airedUtc <= DateTime.UtcNow
                && _subscriptions.IsSubscribed(e.SeasonId, user.Id)
                && IsPlayable(e))
            .GroupBy(e => e.SeriesId)
            .Select(g => g.OrderByDescending(e => e.FirstAiring ?? e.PremiereDate).ThenByDescending(e => e.IndexNumber).First())
            .OrderByDescending(e => e.FirstAiring ?? e.PremiereDate)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();

        var dtoOptions = new DtoOptions { Fields = fields ?? [] };
        return Ok(_dtoService.GetBaseItemDtos(episodes, dtoOptions, user));
    }

    /// <summary>
    /// Gets the fallback download sources of an episode (offered when no configured author released it in time).
    /// </summary>
    /// <param name="itemId">The episode id.</param>
    /// <response code="200">The sources, highest seeded first.</response>
    /// <response code="404">Not an episode.</response>
    /// <returns>An <see cref="OkResult"/> containing the fallback sources.</returns>
    [HttpGet("Items/{itemId}/Sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<TorrentSourceDto>> GetSources([FromRoute, Required] Guid itemId)
    {
        if (GetUserItem(itemId) is not Episode episode)
        {
            return NotFound();
        }

        IReadOnlyList<TorrentSourceDto> sources = (episode.SourceCandidates ?? [])
            .Select((c, i) => new TorrentSourceDto { Index = i, Title = c.Title, Seeders = c.Seeders, SizeBytes = c.SizeBytes })
            .ToList();
        return Ok(sources);
    }

    /// <summary>
    /// Downloads an episode from one of its fallback sources.
    /// </summary>
    /// <param name="itemId">The episode id.</param>
    /// <param name="index">The source index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Download queued.</response>
    /// <response code="400">Invalid source index.</response>
    /// <response code="404">Not an episode.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Items/{itemId}/Sources/{index}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SelectSource(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] int index,
        CancellationToken cancellationToken)
    {
        if (GetUserItem(itemId) is not Episode episode)
        {
            return NotFound();
        }

        return await _subscriptionService.SelectSourceAsync(episode, index, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : BadRequest("Invalid source index.");
    }

    /// <summary>
    /// Subscribes the season of an episode (or a season): new episodes download as soon as an author releases them.
    /// </summary>
    /// <param name="itemId">A season or episode id.</param>
    /// <param name="language">Optional. Anime: Dub ("Dub if available") or Sub. Subscribing again changes it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Subscribed.</response>
    /// <response code="404">No season found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Items/{itemId}/Subscription")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult> Subscribe([FromRoute, Required] Guid itemId, [FromQuery] ReleaseLanguage? language, CancellationToken cancellationToken)
        => SetSubscribed(itemId, true, language, cancellationToken);

    /// <summary>
    /// Gets the language the anime subscribe prompt preselects ("Prioritize Dub" in the Jackett settings).
    /// </summary>
    /// <response code="200">The language.</response>
    /// <returns>An <see cref="OkResult"/> containing the language.</returns>
    [HttpGet("Subscriptions/DefaultLanguage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ReleaseLanguage> GetDefaultSubscriptionLanguage() => _configurationStore.GetJackett().DefaultLanguage;

    /// <summary>
    /// Unsubscribes the season of an episode (or a season).
    /// </summary>
    /// <param name="itemId">A season or episode id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Unsubscribed.</response>
    /// <response code="404">No season found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Items/{itemId}/Subscription")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult> Unsubscribe([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
        => SetSubscribed(itemId, false, null, cancellationToken);

    /// <summary>
    /// Gets how a series (title) or season (season number) is searched for on Jackett.
    /// </summary>
    /// <param name="itemId">A series or season id.</param>
    /// <response code="200">The search settings.</response>
    /// <response code="404">No series or season found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="SearchOverrideDto"/>.</returns>
    [HttpGet("Items/{itemId}/SearchOverride")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SearchOverrideDto> GetSearchOverride([FromRoute, Required] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return item is Series or Season ? Ok(ToSearchOverride(item)) : NotFound();
    }

    /// <summary>
    /// Saves the Jackett search title of a series, or the search season number of a season. Only the search uses it.
    /// </summary>
    /// <param name="itemId">A series or season id.</param>
    /// <param name="request">The title (series) or season number (season).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Saved; the new search settings.</response>
    /// <response code="400">Missing or invalid value.</response>
    /// <response code="404">No series or season found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="SearchOverrideDto"/>.</returns>
    [HttpPost("Items/{itemId}/SearchOverride")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SearchOverrideDto>> SaveSearchOverride(
        [FromRoute, Required] Guid itemId,
        [FromBody, Required] SaveSearchOverrideDto request,
        CancellationToken cancellationToken)
    {
        switch (_libraryManager.GetItemById(itemId))
        {
            case Series series:
                var title = request.Title?.Trim() ?? string.Empty;
                if (_torrentSearch.CleanSearchText(title).Length == 0)
                {
                    return BadRequest("Enter a search title with at least one letter or digit.");
                }

                series.SearchTitleOverride = title;
                await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                return Ok(ToSearchOverride(series));
            case Season season:
                if (request.SeasonNumber is not { } number || number < 0 || number > 999)
                {
                    return BadRequest("Enter a season number from 0 to 999.");
                }

                season.SearchSeasonNumberOverride = number;
                await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                return Ok(ToSearchOverride(season));
            default:
                return NotFound();
        }
    }

    /// <summary>
    /// Removes the search override of a series (title) or season (season number), so the default is searched again.
    /// </summary>
    /// <param name="itemId">A series or season id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Reset; the default search settings.</response>
    /// <response code="404">No series or season found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="SearchOverrideDto"/>.</returns>
    [HttpDelete("Items/{itemId}/SearchOverride")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SearchOverrideDto>> ResetSearchOverride([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        switch (_libraryManager.GetItemById(itemId))
        {
            case Series series:
                series.SearchTitleOverride = string.Empty;
                await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                return Ok(ToSearchOverride(series));
            case Season season:
                season.SearchSeasonNumberOverride = null;
                await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                return Ok(ToSearchOverride(season));
            default:
                return NotFound();
        }
    }

    /// <summary>
    /// Searches the episodes of a series or season again (after its search settings changed): the releases found so far
    /// are forgotten. Downloaded episodes are deleted and downloaded again only when asked; downloading ones are left alone.
    /// </summary>
    /// <param name="itemId">A series or season id.</param>
    /// <param name="replaceDownloaded">Whether downloaded episodes are replaced too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">What will be searched again.</response>
    /// <response code="404">No series or season found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="ResearchResultDto"/>.</returns>
    [HttpPost("Items/{itemId}/Research")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResearchResultDto>> Research(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool replaceDownloaded,
        CancellationToken cancellationToken)
    {
        var seasons = _libraryManager.GetItemById(itemId) switch
        {
            Series series => series.Children.OfType<Season>().ToList(),
            Season season => [season],
            _ => null
        };
        if (seasons is null)
        {
            return NotFound();
        }

        var episodes = seasons.SelectMany(s => s.Children.OfType<Episode>()).ToList();
        var (searching, replaced, skipped) = await _subscriptionService.ResearchAsync(episodes, replaceDownloaded, cancellationToken).ConfigureAwait(false);
        return Ok(new ResearchResultDto { Searching = searching, Replaced = replaced, SkippedDownloading = skipped });
    }

    /// <summary>
    /// Gets change tokens for an item and its children, so a page opened while PirateFin is still fetching metadata
    /// and images can update the parts that changed without being reloaded.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">The tokens.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="ChangeTokenDto"/>.</returns>
    [HttpGet("Items/{itemId}/ChangeToken")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ChangeTokenDto> GetChangeToken([FromRoute, Required] Guid itemId)
    {
        if (GetUserItem(itemId) is not { } item)
        {
            return NotFound();
        }

        // Every metadata / image save bumps DateLastSaved. Adding a child changes the count, removing one the latest save.
        // A series page shows its seasons with their episode counts (and next up), so a series watches its episodes too.
        IReadOnlyList<BaseItem> children = item switch
        {
            Series => _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [item.Id],
                IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            }),
            Folder => _libraryManager.GetItemList(new InternalItemsQuery { ParentId = item.Id, DtoOptions = new DtoOptions(false) }),
            _ => []
        };
        var latestChildSave = children.Count == 0 ? 0 : children.Max(c => c.DateLastSaved.Ticks);

        return new ChangeTokenDto
        {
            Item = item.DateLastSaved.Ticks.ToString(CultureInfo.InvariantCulture),
            Children = string.Create(CultureInfo.InvariantCulture, $"{children.Count}:{latestChildSave}")
        };
    }

    /// <summary>
    /// Gets whether an item can be played yet (a downloading episode becomes playable part way through).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">Whether the item is playable.</response>
    /// <returns>An <see cref="OkResult"/> containing a value indicating whether the item can be played.</returns>
    [HttpGet("Items/{itemId}/Playable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<bool> GetPlayable([FromRoute, Required] Guid itemId)
        => GetUserItem(itemId) is Video video && IsPlayable(video);

    /// <summary>
    /// Gets whether an item can be played yet and how far its download is. For a season or series: whether any of its
    /// episodes can be played.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">The download status.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="DownloadProgressInfo"/>.</returns>
    [HttpGet("Items/{itemId}/DownloadStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DownloadProgressInfo> GetDownloadStatus([FromRoute, Required] Guid itemId)
    {
        switch (GetUserItem(itemId))
        {
            case Video video:
                return _downloadProgress.Get(video);
            case Folder folder when folder is Season or Series:
                var anyPlayable = _libraryManager.GetItemList(new InternalItemsQuery(GetUser(null))
                    {
                        AncestorIds = [folder.Id],
                        IncludeItemTypes = [BaseItemKind.Episode],
                        Recursive = true
                    })
                    .OfType<Video>()
                    .Any(IsPlayable);
                return new DownloadProgressInfo { ItemId = itemId, Playable = anyPlayable, PlayableFill = anyPlayable ? 1 : 0 };
            default:
                return new DownloadProgressInfo { ItemId = itemId };
        }
    }

    /// <summary>
    /// Gets whether an item's downloads are paused because no drive of its library has the minimum free space.
    /// </summary>
    /// <param name="itemId">An episode, movie, season or series id.</param>
    /// <response code="200">The status.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="StorageStatusDto"/>.</returns>
    [HttpGet("Items/{itemId}/StorageStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<StorageStatusDto> GetStorageStatus([FromRoute, Required] Guid itemId)
        => GetUserItem(itemId) is { } item ? new StorageStatusDto { Paused = !_storage.CanStore(item) } : NotFound();

    /// <summary>
    /// Gets the folders of the PirateFin libraries and the free space of their drives.
    /// </summary>
    /// <response code="200">The folders.</response>
    /// <returns>An <see cref="OkResult"/> containing the folders.</returns>
    [HttpGet("Storage")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<StorageLocation>> GetStorage() => Ok(_storage.GetLocations());

    /// <summary>
    /// Gets the series split across drives, which are moved back onto one drive in the night window.
    /// </summary>
    /// <response code="200">The series.</response>
    /// <returns>An <see cref="OkResult"/> containing the series.</returns>
    [HttpGet("Storage/Split")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SplitSeries>> GetSplitSeries() => Ok(_rebalancer.GetSplitSeries());

    /// <summary>
    /// Gets the raw weekly schedule of a provider, for troubleshooting.
    /// </summary>
    /// <param name="library">Shows (Trakt), Anime (AniList) or Movies (YTS).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The schedule.</response>
    /// <response code="400">Unknown library.</response>
    /// <returns>An <see cref="OkResult"/> containing the provider schedule.</returns>
    [HttpGet("Schedule/{library}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<object>>> GetSchedule([FromRoute, Required] string library, CancellationToken cancellationToken)
    {
        IEnumerable<object>? schedule = PirateFinLibraries.Normalize(library) switch
        {
            PirateFinLibraries.Shows => await _scheduleFetcher.GetTvWeeklyScheduleAsync(cancellationToken).ConfigureAwait(false),
            PirateFinLibraries.Anime => await _scheduleFetcher.GetAnimeWeeklyScheduleAsync(cancellationToken).ConfigureAwait(false),
            PirateFinLibraries.Movies => await _scheduleFetcher.GetLatestMoviesAsync(cancellationToken).ConfigureAwait(false),
            _ => null
        };

        return schedule is null ? BadRequest("Unknown library.") : Ok(schedule);
    }

    /// <summary>
    /// Runs one pass of the episode source search now instead of waiting for the background loop.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">The pass finished.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Subscriptions/Process")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ProcessSubscriptions(CancellationToken cancellationToken)
    {
        await _subscriptionService.ProcessDueEpisodesAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes every placeholder of a library that is not downloaded or downloading (and seasons / series left empty),
    /// then fetches the newest items for it again in the background.
    /// </summary>
    /// <param name="library">Shows, Anime or Movies.</param>
    /// <response code="200">What was removed; the refresh continues in the background.</response>
    /// <response code="400">Unknown library.</response>
    /// <response code="409">A reset of this library is already running.</response>
    /// <returns>An <see cref="OkResult"/> containing the removed item counts.</returns>
    [HttpPost("Libraries/{library}/Reset")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<PlaceholderRemovalResult> ResetLibrary([FromRoute, Required] string library)
    {
        var normalized = PirateFinLibraries.Normalize(library);
        if (normalized is null)
        {
            return BadRequest("Library must be Shows, Anime or Movies.");
        }

        var result = _libraryService.StartReset(normalized);
        return result is null ? Conflict($"{normalized} is already being refreshed.") : Ok(result);
    }

    /// <summary>
    /// Gets the Trakt / AniList / YTS settings (providers.json).
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>An <see cref="OkResult"/> containing the provider settings.</returns>
    [HttpGet("Configuration/Providers")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<InternetProviderOptions> GetProvidersConfiguration() => _configurationStore.GetProviders();

    /// <summary>
    /// Saves the Trakt / AniList / YTS settings (providers.json).
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Invalid settings.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Configuration/Providers")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveProvidersConfiguration([FromBody, Required] InternetProviderOptions options, CancellationToken cancellationToken)
    {
        var error = ValidateUrls(("AniList GraphQL url", options.AniListGraphQlUrl), ("Trakt API url", options.TraktApiUrl), ("YTS API url", options.YtsApiUrl));
        if (error is not null)
        {
            return BadRequest(error);
        }

        await _configurationStore.SaveProvidersAsync(options, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the Jackett settings (jackett.json).
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>An <see cref="OkResult"/> containing the Jackett settings.</returns>
    [HttpGet("Configuration/Jackett")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<JackettOptions> GetJackettConfiguration() => _configurationStore.GetJackett();

    /// <summary>
    /// Saves the Jackett settings (jackett.json).
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Invalid settings.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Configuration/Jackett")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveJackettConfiguration([FromBody, Required] JackettOptions options, CancellationToken cancellationToken)
    {
        var error = ValidateUrls(("Jackett url", options.BaseUrl));
        if (error is not null)
        {
            return BadRequest(error);
        }

        await _configurationStore.SaveJackettAsync(options, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the qBittorrent settings (qbittorrent.json).
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>An <see cref="OkResult"/> containing the qBittorrent settings.</returns>
    [HttpGet("Configuration/QBitTorrent")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QBitTorrentOptions> GetQBitTorrentConfiguration() => _configurationStore.GetQBitTorrent();

    /// <summary>
    /// Saves the qBittorrent settings (qbittorrent.json).
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Invalid settings.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Configuration/QBitTorrent")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveQBitTorrentConfiguration([FromBody, Required] QBitTorrentOptions options, CancellationToken cancellationToken)
    {
        var error = ValidateUrls(("qBittorrent Web UI url", options.Url));
        if (error is null && options.PlayableProgress is < 0 or > 1)
        {
            error = "The playable progress must be between 0 and 1.";
        }

        if (error is null && options.MinimumFreeSpaceGb < 0)
        {
            error = "The minimum free space can't be negative.";
        }

        if (error is null
            && (!TimeOnly.TryParse(options.StorageMoveStart, CultureInfo.InvariantCulture, out _)
                || !TimeOnly.TryParse(options.StorageMoveEnd, CultureInfo.InvariantCulture, out _)))
        {
            error = "The move window times must look like 02:00.";
        }

        if (error is not null)
        {
            return BadRequest(error);
        }

        await _configurationStore.SaveQBitTorrentAsync(options, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes an item from the library without touching its files.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">Removed.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Items/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ForceDeleteItem([FromRoute, Required] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        return NoContent();
    }

    private static string? ValidateUrls(params (string Name, string Value)[] urls)
    {
        foreach (var (name, value) in urls)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return $"The {name} must be an absolute http(s) url.";
            }
        }

        return null;
    }

    private SearchOverrideDto ToSearchOverride(BaseItem item)
    {
        var series = item as Series ?? (item as Season)?.Series;
        var dto = new SearchOverrideDto();
        if (series is not null)
        {
            dto.DefaultTitle = _torrentSearch.GetDefaultSearchTitle(series);
            dto.IsTitleOverridden = !string.IsNullOrWhiteSpace(series.SearchTitleOverride);
            dto.Title = dto.IsTitleOverridden ? series.SearchTitleOverride : dto.DefaultTitle;
        }

        if (item is Season season)
        {
            dto.DefaultSeasonNumber = season.IndexNumber ?? 1;
            dto.IsSeasonNumberOverridden = season.SearchSeasonNumberOverride.HasValue;
            dto.SeasonNumber = season.SearchSeasonNumberOverride ?? dto.DefaultSeasonNumber;
        }

        return dto;
    }

    private static bool IsAdult(Series? series)
        => series is not null
            && (series.Genres.Intersect(_adultGenres, StringComparer.OrdinalIgnoreCase).Any()
                || series.Tags.Intersect(_adultTags, StringComparer.OrdinalIgnoreCase).Any());

    private async Task<ActionResult> SetSubscribed(Guid itemId, bool subscribed, ReleaseLanguage? language, CancellationToken cancellationToken)
    {
        var season = GetUserItem(itemId) switch
        {
            Season s => s,
            Episode e => e.Season,
            _ => null
        };
        if (season is null || GetUser(null) is not { } user)
        {
            return NotFound();
        }

        await _subscriptionService.SetSubscribedAsync(season, user.Id, subscribed, language, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private User? GetUser(Guid? userId)
    {
        var id = RequestHelpers.GetUserId(User, userId);
        return _userManager.GetUserById(id);
    }

    private bool IsPlayable(Video video) => _downloadProgress.IsPlayable(video);

    private BaseItem? GetUserItem(Guid itemId)
    {
        var user = GetUser(null);
        return user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
    }

    private Guid? GetCollectionFolderId(string library)
    {
        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));
        return folder is not null && Guid.TryParse(folder.ItemId, out var id) ? id : null;
    }
}
