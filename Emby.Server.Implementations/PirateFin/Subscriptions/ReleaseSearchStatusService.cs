using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// Sorts the episodes waiting for a release search the way the subscription search treats them: new (aired in the
/// last week, retried until a release turns up), old (one search each, the back catalogue pass) and not aired yet.
/// </summary>
public sealed class ReleaseSearchStatusService : IReleaseSearchStatus
{
    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IReleaseSearchPauses _pauses;
    private readonly IOptionsMonitor<SubscriptionOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReleaseSearchStatusService> _logger;
    private readonly Lock _lock = new();
    private ReleaseSearchStatus? _cached;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReleaseSearchStatusService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="subscriptionService">The subscription search (what's being searched now, recent results).</param>
    /// <param name="pauses">The paused old searches.</param>
    /// <param name="options">The subscription options.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public ReleaseSearchStatusService(
        ILibraryManager libraryManager,
        ISubscriptionService subscriptionService,
        IReleaseSearchPauses pauses,
        IOptionsMonitor<SubscriptionOptions> options,
        TimeProvider timeProvider,
        ILogger<ReleaseSearchStatusService> logger)
    {
        _libraryManager = libraryManager;
        _subscriptionService = subscriptionService;
        _pauses = pauses;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private enum Group
    {
        New,
        Old,
        NotAired
    }

    /// <inheritdoc />
    public ReleaseSearchStatus GetStatus()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        ReleaseSearchStatus counts;
        lock (_lock)
        {
            if (_cached is null || now - _cached.CalculatedUtc >= _cacheLifetime)
            {
                _cached = Calculate(now);
            }

            counts = _cached;
        }

        // What's being searched and the results change by the second; they're cheap.
        var (preferred, fallbackOnly, nothing, since) = _subscriptionService.GetRecentResults();
        return new ReleaseSearchStatus
        {
            CalculatedUtc = counts.CalculatedUtc,
            SearchingNow = GetSearchingNow(),
            NewEpisodes = counts.NewEpisodes,
            OldEpisodes = counts.OldEpisodes,
            DueNow = counts.DueNow,
            WaitingToAir = counts.WaitingToAir,
            OldPaused = counts.OldPaused,
            OldPassEndsUtc = counts.OldPassEndsUtc,
            Shows = counts.Shows,
            FoundPreferred = preferred,
            FoundFallbackOnly = fallbackOnly,
            FoundNothing = nothing,
            ResultsSinceUtc = since
        };
    }

    /// <inheritdoc />
    public async Task<int> StopOldSearchesAsync(Guid? seriesId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var searching = _subscriptionService.GetSearchingNow().ToHashSet();
        var old = GetWaiting()
            .Where(e => (!seriesId.HasValue || e.SeriesId.Equals(seriesId.Value))
                && Classify(e, now) == Group.Old
                && !searching.Contains(e.Id))
            .ToList();
        foreach (var episode in old)
        {
            episode.Scheduled = false;
            episode.NextSourceSearchUtc = null;
        }

        await SaveAsync(old, cancellationToken).ConfigureAwait(false);

        // Nothing of it is left to pause; a pause kept would hold back old episodes a later refresh adds.
        if (seriesId.HasValue)
        {
            _pauses.ResumeShow(seriesId.Value);
        }

        _logger.LogInformation("Stopped the release searches of {Count} older episodes", old.Count);
        ClearCache();
        return old.Count;
    }

    /// <inheritdoc />
    public void PauseOldSearches(Guid? seriesId)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (seriesId.HasValue)
        {
            _pauses.PauseShow(seriesId.Value, now);
        }
        else
        {
            _pauses.PauseAll(now);
        }

        _logger.LogInformation("Paused the old release searches of {Scope}", seriesId?.ToString() ?? "every show");
        ClearCache();
    }

    /// <inheritdoc />
    public async Task ResumeOldSearchesAsync(Guid? seriesId, CancellationToken cancellationToken)
    {
        var pausedAt = seriesId.HasValue ? _pauses.ResumeShow(seriesId.Value) : _pauses.ResumeAll();
        ClearCache();
        if (!pausedAt.HasValue)
        {
            return;
        }

        // The pass's searches are spaced out; moving them on by the pause keeps that spacing instead of every
        // search that fell due meanwhile running at once. Ones still paused (by the other pause) move on when resumed.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var by = now - pausedAt.Value;
        if (by <= TimeSpan.Zero)
        {
            return;
        }

        var searching = _subscriptionService.GetSearchingNow().ToHashSet();
        var resumed = GetWaiting()
            .Where(e => (!seriesId.HasValue || e.SeriesId.Equals(seriesId.Value))
                && e.NextSourceSearchUtc.HasValue
                && Classify(e, now) == Group.Old
                && !_pauses.IsPaused(e.SeriesId)
                && !searching.Contains(e.Id))
            .ToList();
        foreach (var episode in resumed)
        {
            episode.NextSourceSearchUtc += by;
        }

        await SaveAsync(resumed, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Resumed the old release searches of {Scope}; {Count} searches moved on by {Paused}",
            seriesId?.ToString() ?? "every show",
            resumed.Count,
            by);
        ClearCache();
    }

    /// <summary>
    /// Counts the waiting episodes by group and by show.
    /// </summary>
    /// <param name="now">The current time, in UTC.</param>
    /// <returns>The status (without what's being searched now and the recent results).</returns>
    internal ReleaseSearchStatus Calculate(DateTime now)
    {
        var status = new ReleaseSearchStatus { CalculatedUtc = now, OldPaused = _pauses.AllPausedUtc.HasValue };
        var shows = new Dictionary<Guid, ReleaseSearchShow>();
        foreach (var episode in GetWaiting())
        {
            var group = Classify(episode, now);
            if (group == Group.NotAired)
            {
                status.WaitingToAir++;
                continue;
            }

            if (!shows.TryGetValue(episode.SeriesId, out var show))
            {
                show = new ReleaseSearchShow
                {
                    SeriesId = episode.SeriesId,
                    SeriesName = episode.SeriesName ?? string.Empty,
                    Paused = _pauses.GetShowPausedUtc(episode.SeriesId).HasValue
                };
                shows[episode.SeriesId] = show;
            }

            var next = episode.NextSourceSearchUtc;
            if (group == Group.New)
            {
                status.NewEpisodes++;
                show.NewEpisodes++;
            }
            else
            {
                status.OldEpisodes++;
                show.OldEpisodes++;

                // Paused: counted, but not due and no next search.
                if (status.OldPaused || show.Paused)
                {
                    continue;
                }

                if (next.HasValue && (!status.OldPassEndsUtc.HasValue || next.Value > status.OldPassEndsUtc.Value))
                {
                    status.OldPassEndsUtc = next.Value;
                }
            }

            if (!next.HasValue || next.Value <= now)
            {
                status.DueNow++;
            }

            // Due ones count as "now"; a show's next search is its earliest.
            var at = next.HasValue && next.Value > now ? next.Value : now;
            if (!show.NextSearchUtc.HasValue || at < show.NextSearchUtc.Value)
            {
                show.NextSearchUtc = at;
            }
        }

        status.Shows = shows.Values
            .OrderBy(s => s.NextSearchUtc ?? DateTime.MaxValue)
            .ThenBy(s => s.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return status;
    }

    // The same episodes the subscription search picks from, aired or not.
    private IEnumerable<Episode> GetWaiting()
        => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsInternet = true,
                Scheduled = true
            })
            .OfType<Episode>()
            .Where(e => !e.IsDownloaded && !e.DownloadStartedUtc.HasValue && string.IsNullOrEmpty(e.DownloadUrl));

    private Group Classify(Episode episode, DateTime now)
    {
        if (!episode.FirstAiring.HasValue || ReleaseStatusResolver.ToUtc(episode.FirstAiring.Value) > now)
        {
            return Group.NotAired;
        }

        return SubscriptionService.IsOld(episode, now, _options.CurrentValue) ? Group.Old : Group.New;
    }

    // Saved a season at a time; the back catalogue can be tens of thousands of episodes.
    private async Task SaveAsync(List<Episode> episodes, CancellationToken cancellationToken)
    {
        foreach (var season in episodes.GroupBy(e => e.ParentId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = _libraryManager.GetItemById(season.Key);
            if (parent is null)
            {
                continue;
            }

            await _libraryManager.UpdateItemsAsync(season.ToList(), parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ClearCache()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }

    private List<ReleaseSearchEpisode> GetSearchingNow()
        => _subscriptionService.GetSearchingNow()
            .Select(id => _libraryManager.GetItemById(id))
            .OfType<Episode>()
            .Select(e => new ReleaseSearchEpisode
            {
                SeriesName = e.SeriesName ?? string.Empty,
                Episode = $"S{e.ParentIndexNumber ?? 0}E{e.IndexNumber ?? 0}",
                Name = e.Name ?? string.Empty
            })
            .ToList();
}
