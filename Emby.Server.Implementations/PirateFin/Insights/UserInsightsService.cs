using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Insights;

/// <summary>
/// <see cref="IUserInsights"/> from the playback log, the users' played state, subscriptions, requests and searches.
/// </summary>
public sealed class UserInsightsService : IUserInsights
{
    /// <summary>
    /// A title not played for this many days counts as left (a drop-off); played within it, as being watched.
    /// </summary>
    public const int DropOffDays = 30;

    /// <summary>
    /// A movie stopped before this share of its run time counts as unfinished.
    /// </summary>
    public const double MovieFinished = 0.9;

    private const int ListLimit = 20;

    private static readonly string[] _libraries = [PirateFinLibraries.Shows, PirateFinLibraries.Anime, PirateFinLibraries.Movies];

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaybackHistory _history;
    private readonly IGenreRanking _genreRanking;
    private readonly ISeasonSubscriptions _subscriptions;
    private readonly IDownloadRequests _requests;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UserInsightsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserInsightsService"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="history">The playback log.</param>
    /// <param name="genreRanking">The genre ranking.</param>
    /// <param name="subscriptions">The season subscriptions.</param>
    /// <param name="requests">The download requests.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public UserInsightsService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IPlaybackHistory history,
        IGenreRanking genreRanking,
        ISeasonSubscriptions subscriptions,
        IDownloadRequests requests,
        TimeProvider timeProvider,
        ILogger<UserInsightsService> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _history = history;
        _genreRanking = genreRanking;
        _subscriptions = subscriptions;
        _requests = requests;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public UserInsights? GetInsights(Guid userId, TimeSpan utcOffset)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var today = (now + utcOffset).Date;
        var sessions = _history.GetSessions(userId, now.AddDays(-InsightCalculator.Days - 1), now.AddDays(1));

        var hours = InsightCalculator.SpreadOverHours(sessions, utcOffset);
        var firstHour = today.AddDays(1 - InsightCalculator.Days);
        foreach (var old in hours.Keys.Where(h => h < firstHour).ToList())
        {
            hours.Remove(old);
        }

        var daily = InsightCalculator.GetDaily(hours, today);
        var genres = _libraries
            .Where(library => GetCollectionFolderId(library).HasValue)
            .Select(library => _genreRanking.GetRanking(userId, library))
            .ToList();

        var (rewatches, watching, dropOffs) = GetPlayedTitles(user, now);
        var requests = _requests.GetForUser(userId).OrderByDescending(r => r.CreatedUtc).ToList();
        var failedSearches = _history.GetSearches(userId, now.AddDays(-InsightCalculator.Days), now.AddDays(1))
            .Where(s => s.Results == 0)
            .ToList();
        var lastWatched = sessions.Count > 0 ? sessions.Max(s => s.EndUtc) : (DateTime?)null;

        return new UserInsights
        {
            UserId = userId,
            Activity = new ActivitySummary
            {
                LastActiveUtc = user.LastActivityDate,
                LastWatchedUtc = lastWatched,
                StreakDays = InsightCalculator.GetStreak(daily),
                ActiveDays30 = InsightCalculator.GetActiveDays(daily, 30),
                Seconds7 = daily.TakeLast(7).Sum(d => d.Seconds),
                Seconds30 = daily.TakeLast(30).Sum(d => d.Seconds),
                Seconds90 = daily.Sum(d => d.Seconds),
                Libraries = sessions
                    .GroupBy(s => s.Library ?? "Other")
                    .Select(g => new LibraryWatchTime { Library = g.Key, Seconds = g.Sum(s => s.WatchedSeconds) })
                    .Where(l => l.Seconds > 0)
                    .OrderByDescending(l => l.Seconds)
                    .ToList()
            },
            Daily = daily,
            Heatmap = InsightCalculator.GetHeatmap(hours),
            Genres = genres,
            TopTitles = InsightCalculator.GetTopTitles(sessions, 10),
            Rewatches = rewatches,
            CurrentlyWatching = watching,
            DropOffs = dropOffs,
            Subscriptions = GetSubscriptions(userId),
            Requests = requests,
            FailedSearches = failedSearches,
            Suggestions = InsightCalculator.GetSuggestions(genres, GetLibraryGenreTitles(genres), failedSearches, dropOffs, requests, lastWatched, now)
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<UserInsightSummary> GetSummaries()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var libraries = _libraries.Where(library => GetCollectionFolderId(library).HasValue).ToList();
        return _userManager.GetUsers()
            .Select(user => new UserInsightSummary
            {
                UserId = user.Id,
                Seconds7 = _history.GetSessions(user.Id, now.AddDays(-7), now.AddDays(1)).Sum(s => s.WatchedSeconds),
                TopGenre = libraries
                    .SelectMany(library => _genreRanking.GetRanking(user.Id, library).Genres)
                    .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Sum(x => x.Titles))
                    .Select(g => g.Key)
                    .FirstOrDefault()
            })
            .ToList();
    }

    private (List<InsightTitle> Rewatches, List<InsightTitle> Watching, List<InsightTitle> DropOffs) GetPlayedTitles(User user, DateTime now)
    {
        var rewatches = new List<InsightTitle>();
        var watching = new List<InsightTitle>();
        var dropOffs = new List<InsightTitle>();
        var recent = now.AddDays(-DropOffDays);

        // Movies: rewatched, or stopped part way.
        foreach (var movie in Query(user, BaseItemKind.Movie, q => q.IsPlayed = true)
            .Concat(Query(user, BaseItemKind.Movie, q => q.IsResumable = true))
            .DistinctBy(m => m.Id))
        {
            if (_userDataManager.GetUserData(user, movie) is not { } data)
            {
                continue;
            }

            if (data.PlayCount >= 2)
            {
                rewatches.Add(ToTitle(movie, "Movie", movie.Name, data.PlayCount, data.LastPlayedDate, null));
            }

            if (!data.Played && movie.RunTimeTicks is > 0 && data.PlaybackPositionTicks > 0
                && (double)data.PlaybackPositionTicks / movie.RunTimeTicks.Value < MovieFinished)
            {
                var progress = string.Create(CultureInfo.InvariantCulture, $"{(int)(100.0 * data.PlaybackPositionTicks / movie.RunTimeTicks.Value)}%");
                var title = ToTitle(movie, "Movie", movie.Name, data.PlayCount, data.LastPlayedDate, progress);
                (data.LastPlayedDate >= recent ? watching : dropOffs).Add(title);
            }
        }

        // Series: the episodes played (or in progress) per series.
        var episodes = Query(user, BaseItemKind.Episode, q => q.IsPlayed = true)
            .Concat(Query(user, BaseItemKind.Episode, q => q.IsResumable = true))
            .OfType<Episode>()
            .DistinctBy(e => e.Id)
            .Select(e => (Episode: e, Data: _userDataManager.GetUserData(user, e)))
            .Where(e => e.Data is not null)
            .ToList();

        foreach (var (episode, data) in episodes.Where(e => e.Data!.PlayCount >= 2))
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{episode.SeriesName} S{episode.ParentIndexNumber}:E{episode.IndexNumber}");
            rewatches.Add(ToTitle(episode, "Episode", name, data!.PlayCount, data.LastPlayedDate, null));
        }

        foreach (var series in episodes
            .Where(e => !e.Episode.SeriesId.IsEmpty())
            .GroupBy(e => e.Episode.SeriesId)
            .Select(g => (SeriesId: g.Key, Played: g.Count(e => e.Data!.Played), LastPlayed: g.Max(e => e.Data!.LastPlayedDate)))
            .OrderByDescending(s => s.LastPlayed)
            .Take(200))
        {
            if (_libraryManager.GetItemById(series.SeriesId) is not Series item)
            {
                continue;
            }

            // Episodes that have aired and aren't played yet.
            var left = Query(user, BaseItemKind.Episode, q =>
                {
                    q.ParentId = item.Id;
                    q.IsPlayed = false;
                })
                .OfType<Episode>()
                .Count(e => (e.FirstAiring ?? e.PremiereDate) is { } aired && aired <= now);
            if (left == 0)
            {
                continue;
            }

            var progress = string.Create(CultureInfo.InvariantCulture, $"{series.Played} of {series.Played + left} episodes");
            var title = ToTitle(item, "Series", item.Name, 0, series.LastPlayed, progress);
            (series.LastPlayed >= recent ? watching : dropOffs).Add(title);
        }

        return (
            rewatches.OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayedUtc).Take(ListLimit).ToList(),
            watching.OrderByDescending(t => t.LastPlayedUtc).Take(ListLimit).ToList(),
            dropOffs.OrderByDescending(t => t.LastPlayedUtc).Take(ListLimit).ToList());
    }

    private InsightTitle ToTitle(BaseItem item, string type, string name, int playCount, DateTime? lastPlayed, string? progress)
        => new()
        {
            ItemId = item.Id,
            Name = name,
            Type = type,
            Library = GetLibrary(item),
            Genres = (item is Episode episode ? episode.Series?.Genres : item.Genres) ?? [],
            Progress = progress,
            PlayCount = playCount,
            LastPlayedUtc = lastPlayed
        };

    private List<InsightSubscription> GetSubscriptions(Guid userId)
        => _subscriptions.GetSubscriptions(userId)
            .Select(s => (Season: _libraryManager.GetItemById(s.SeasonId) as Season, s.SinceUtc))
            .Where(s => s.Season is not null)
            .Select(s => new InsightSubscription
            {
                SeasonId = s.Season!.Id,
                SeriesId = s.Season.SeriesId,
                SeriesName = s.Season.SeriesName ?? string.Empty,
                SeasonName = s.Season.Name ?? string.Empty,
                Library = GetLibrary(s.Season),
                SinceUtc = s.SinceUtc
            })
            .OrderByDescending(s => s.SinceUtc)
            .ToList();

    // Titles per genre in each library, for the user's top genres only.
    private Dictionary<string, IReadOnlyDictionary<string, int>> GetLibraryGenreTitles(IReadOnlyList<GenreRanking> genres)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ranking in genres)
        {
            var folderId = GetCollectionFolderId(ranking.Library);
            if (folderId is null)
            {
                continue;
            }

            var kind = ranking.Library == PirateFinLibraries.Movies ? BaseItemKind.Movie : BaseItemKind.Series;
            result[ranking.Library] = ranking.Genres.Take(3).ToDictionary(
                g => g.Name,
                g => _libraryManager.GetCount(new InternalItemsQuery
                {
                    ParentId = folderId.Value,
                    Recursive = true,
                    IncludeItemTypes = [kind],
                    Genres = [g.Name]
                }),
                StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private IReadOnlyList<BaseItem> Query(User user, BaseItemKind kind, Action<InternalItemsQuery> filter)
    {
        var query = new InternalItemsQuery(user) { IncludeItemTypes = [kind], Recursive = true };
        filter(query);
        return _libraryManager.GetItemList(query);
    }

    private string? GetLibrary(BaseItem item)
    {
        try
        {
            return _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't find the library of {Item}", item.Name);
            return null;
        }
    }

    private Guid? GetCollectionFolderId(string library)
    {
        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));
        return folder is not null && Guid.TryParse(folder.ItemId, out var id) ? id : null;
    }
}
