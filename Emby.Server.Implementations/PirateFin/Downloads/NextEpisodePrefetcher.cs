using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Downloads;

/// <summary>
/// Once a user has watched half of an episode: subscribes them to its season (so they hear about new episodes), looks
/// the series up on Trakt / AniList for seasons and episodes the library is missing, and downloads the next
/// <see cref="EpisodesAhead"/> episodes, so "Play next episode" can play them straight away.
/// </summary>
/// <remarks>
/// The next episodes are taken in order and only while each one is available: watching episode 7 downloads 8 and 9, but
/// nothing when 8 can't be downloaded. A user who turned a season's subscription off isn't subscribed again.
/// </remarks>
public sealed class NextEpisodePrefetcher : IHostedService
{
    private const double TriggerAt = 0.5;
    private const int EpisodesAhead = 2;

    private static readonly TimeSpan _refreshInterval = TimeSpan.FromHours(6);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDownloadManager _downloadManager;
    private readonly ISeasonSubscriptions _subscriptions;
    private readonly IInternetSearchService _searchService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NextEpisodePrefetcher> _logger;

    // "user:episode" pairs already handled while that episode plays; cleared when its playback stops.
    private readonly ConcurrentDictionary<string, byte> _handled = new();

    // When each series was last looked up on Trakt / AniList.
    private readonly ConcurrentDictionary<Guid, DateTime> _lastRefresh = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NextEpisodePrefetcher"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="subscriptions">Which users follow which seasons.</param>
    /// <param name="searchService">Looks series up on Trakt / AniList.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public NextEpisodePrefetcher(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IDownloadManager downloadManager,
        ISeasonSubscriptions subscriptions,
        IInternetSearchService searchService,
        TimeProvider timeProvider,
        ILogger<NextEpisodePrefetcher> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _downloadManager = downloadManager;
        _subscriptions = subscriptions;
        _searchService = searchService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private static string Key(Guid userId, Guid episodeId)
        => string.Create(CultureInfo.InvariantCulture, $"{userId:N}:{episodeId:N}");

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        foreach (var user in e.Users)
        {
            _handled.TryRemove(Key(user.Id, e.Item.Id), out _);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Episode episode
            || episode.RunTimeTicks is not > 0
            || e.PlaybackPositionTicks is not { } position
            || (double)position / episode.RunTimeTicks.Value < TriggerAt)
        {
            return;
        }

        var userIds = e.Users.Select(u => u.Id).Where(id => _handled.TryAdd(Key(id, episode.Id), 0)).ToList();
        if (userIds.Count == 0)
        {
            return;
        }

        _ = Task.Run(() => OnHalfWatchedAsync(episode, userIds));
    }

    private async Task OnHalfWatchedAsync(Episode episode, IReadOnlyList<Guid> userIds)
    {
        try
        {
            if (_libraryManager.GetItemById(episode.SeasonId) is Season season)
            {
                foreach (var userId in userIds.Where(id => !_subscriptions.HasUnsubscribed(season.Id, id)))
                {
                    if (await _subscriptions.SubscribeAsync(season, userId, CancellationToken.None).ConfigureAwait(false))
                    {
                        _logger.LogInformation("Subscribed user {UserId} to {Series} {Season}: they're watching it", userId, episode.SeriesName, season.Name);
                    }
                }
            }

            await RefreshSeriesAsync(episode).ConfigureAwait(false);
            QueueNextEpisodes(episode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to follow up on {Series} {Episode}", episode.SeriesName, episode.Name);
        }
    }

    // Fetches the seasons / episodes the library is missing (also for series the user added), at most every few hours.
    private async Task RefreshSeriesAsync(Episode episode)
    {
        if (_libraryManager.GetItemById(episode.SeriesId) is not Series series)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (_lastRefresh.TryGetValue(series.Id, out var last) && now - last < _refreshInterval)
        {
            return;
        }

        _lastRefresh[series.Id] = now;
        var added = await _searchService.RefreshSeriesAsync(series, CancellationToken.None).ConfigureAwait(false);
        if (added is { Episodes: > 0 } result)
        {
            _logger.LogInformation("Added {Seasons} seasons and {Episodes} episodes to {Series}", result.Seasons, result.Episodes, series.Name);
        }
    }

    // The next episodes in order while each is available: downloaded ones are skipped, ones with a source are queued,
    // and the first one that can't be downloaded stops the run.
    private void QueueNextEpisodes(Episode episode)
    {
        foreach (var next in GetNextEpisodes(episode, EpisodesAhead))
        {
            if (next.IsDownloaded)
            {
                continue;
            }

            if (string.IsNullOrEmpty(next.DownloadUrl))
            {
                return;
            }

            if (_downloadManager.QueueDownload(next))
            {
                _logger.LogInformation("{Series} {Episode} is half watched; downloading {Next}", episode.SeriesName, episode.Name, next.Name);
            }
        }
    }

    // The episodes after this one in the series, in season / episode order, skipping specials.
    private IEnumerable<Episode> GetNextEpisodes(Episode episode, int count)
    {
        if (episode.SeriesId.Equals(Guid.Empty) || episode.ParentIndexNumber is not { } season || episode.IndexNumber is not { } number)
        {
            return [];
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [episode.SeriesId],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            })
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber.HasValue)
            .Where(e => e.ParentIndexNumber > season || (e.ParentIndexNumber == season && e.IndexNumber > number))
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber)
            .Take(count)
            .ToList();
    }
}
