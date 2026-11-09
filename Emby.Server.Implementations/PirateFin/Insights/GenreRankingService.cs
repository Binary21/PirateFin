using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Insights;

/// <summary>
/// <see cref="IGenreRanking"/> from the users' played state: a movie counts once it's played or watched at least
/// halfway, a series once at least <see cref="MinSeriesEpisodes"/> of its episodes are played. Rankings are cached for
/// <see cref="CacheFor"/>.
/// </summary>
public sealed class GenreRankingService : IGenreRanking
{
    /// <summary>
    /// The played episodes a series needs before it counts.
    /// </summary>
    public const int MinSeriesEpisodes = 3;

    /// <summary>
    /// How far into a movie the user must be for it to count.
    /// </summary>
    public const double MovieThreshold = 0.5;

    /// <summary>
    /// How long a user's ranking is reused.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GenreRankingService> _logger;
    private readonly ConcurrentDictionary<(Guid UserId, string Library), (DateTime Utc, Dictionary<string, int> Counts)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreRankingService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public GenreRankingService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        TimeProvider timeProvider,
        ILogger<GenreRankingService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public GenreRanking GetRanking(Guid userId, string library)
    {
        var ranking = new GenreRanking { Library = library };
        var user = _userManager.GetUserById(userId);
        var folderId = GetCollectionFolderId(library);
        if (user is null || folderId is null)
        {
            return ranking;
        }

        var own = GetCounts(user, library, folderId.Value);
        ranking.Genres = ToRanked(own.OrderByDescending(g => g.Value).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase));

        // "Rarely watched": anything outside the top half of what this user watches.
        var topHalf = own.OrderByDescending(g => g.Value).Take((own.Count + 1) / 2).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = new Dictionary<string, (int Users, int Titles)>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in _userManager.GetUsers().Where(u => !u.Id.Equals(userId)))
        {
            foreach (var (genre, titles) in GetCounts(other, library, folderId.Value))
            {
                others.TryGetValue(genre, out var total);
                others[genre] = (total.Users + 1, total.Titles + titles);
            }
        }

        ranking.Discovery = ToRanked(others
            .Where(g => !topHalf.Contains(g.Key))
            .OrderByDescending(g => g.Value.Users)
            .ThenByDescending(g => g.Value.Titles)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Value.Titles)));
        return ranking;
    }

    /// <summary>
    /// Counts the titles a user watched per genre in a library.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="library">The library name, for the cache.</param>
    /// <param name="folderId">The library's collection folder.</param>
    /// <returns>Titles per genre.</returns>
    internal Dictionary<string, int> GetCounts(User user, string library, Guid folderId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (_cache.TryGetValue((user.Id, library), out var cached) && now - cached.Utc < CacheFor)
        {
            return cached.Counts;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in GetWatchedTitles(user, folderId))
        {
            foreach (var genre in title.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[genre] = counts.GetValueOrDefault(genre) + 1;
            }
        }

        _cache[(user.Id, library)] = (now, counts);
        return counts;
    }

    private IEnumerable<BaseItem> GetWatchedTitles(User user, Guid folderId)
    {
        // Movies: played, or stopped at least halfway.
        var played = Query(user, folderId, BaseItemKind.Movie, q => q.IsPlayed = true);
        var halfway = Query(user, folderId, BaseItemKind.Movie, q => q.IsResumable = true)
            .Where(movie => movie.RunTimeTicks is > 0
                && _userDataManager.GetUserData(user, movie) is { } data
                && data.PlaybackPositionTicks >= movie.RunTimeTicks.Value * MovieThreshold);
        foreach (var movie in played.Concat(halfway).DistinctBy(m => m.Id))
        {
            yield return movie;
        }

        // Series: enough played episodes.
        var seriesIds = Query(user, folderId, BaseItemKind.Episode, q => q.IsPlayed = true)
            .OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .Where(e => !e.SeriesId.IsEmpty())
            .GroupBy(e => e.SeriesId)
            .Where(g => g.Count() >= MinSeriesEpisodes)
            .Select(g => g.Key)
            .ToArray();
        if (seriesIds.Length > 0)
        {
            foreach (var series in _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = seriesIds }))
            {
                yield return series;
            }
        }
    }

    private IReadOnlyList<BaseItem> Query(User user, Guid folderId, BaseItemKind kind, Action<InternalItemsQuery> filter)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            ParentId = folderId
        };
        filter(query);
        return _libraryManager.GetItemList(query);
    }

    private Guid? GetCollectionFolderId(string library)
    {
        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));
        return folder is not null && Guid.TryParse(folder.ItemId, out var id) ? id : null;
    }

    private List<RankedGenre> ToRanked(IEnumerable<KeyValuePair<string, int>> genres)
    {
        var ranked = new List<RankedGenre>();
        foreach (var (name, titles) in genres)
        {
            try
            {
                ranked.Add(new RankedGenre { Id = _libraryManager.GetGenre(name).Id, Name = name, Titles = titles });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping genre {Genre}", name);
            }
        }

        return ranked;
    }
}
