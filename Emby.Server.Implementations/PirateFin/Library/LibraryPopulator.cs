using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Storage;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Fills in the Shows / Anime series, one at a time with a pause in between (Trakt / AniList limit how fast they're
/// asked): the seasons and episodes they're missing (like the series page's "Find missing seasons and episodes"), a
/// metadata and image refresh, and release searches for the new episodes, spaced out so Jackett isn't flooded (the
/// subscription search picks each one up at its turn). Every night in the storage move window it covers series added
/// in the last 7 days and series still airing; a full run over every series is started from the dashboard.
/// </summary>
public sealed class LibraryPopulator : BackgroundService, ILibraryPopulator
{
    private const string FullRun = "Full";
    private const string NightlyRun = "Nightly";

    private static readonly TimeSpan _recent = TimeSpan.FromDays(7);
    private static readonly TimeSpan _pauseBetweenSeries = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _releaseSearchSpacing = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _checkEvery = TimeSpan.FromMinutes(1);

    // The night run happens once per window; a window is at most a day long.
    private static readonly TimeSpan _nightlyAtMostEvery = TimeSpan.FromHours(20);

    private readonly ILibraryManager _libraryManager;
    private readonly IInternetSearchService _searchService;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IOptionsMonitor<QBitTorrentOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LibraryPopulator> _logger;
    private readonly Channel<bool> _fullRunRequests = Channel.CreateBounded<bool>(1);
    private readonly Lock _lock = new();
    private PopulateStatus _status = new();
    private DateTime _lastNightlyUtc = DateTime.MinValue;
    private DateTime _nextReleaseSearchUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryPopulator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="searchService">Finds the missing seasons and episodes.</param>
    /// <param name="providerManager">The provider manager (metadata refreshes).</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="options">The qBittorrent options (the night window).</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public LibraryPopulator(
        ILibraryManager libraryManager,
        IInternetSearchService searchService,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IOptionsMonitor<QBitTorrentOptions> options,
        TimeProvider timeProvider,
        ILogger<LibraryPopulator> logger)
    {
        _libraryManager = libraryManager;
        _searchService = searchService;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public PopulateStatus GetStatus()
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            return new PopulateStatus
            {
                Running = _status.Running,
                Kind = _status.Kind,
                Total = _status.Total,
                Done = _status.Done,
                Failed = _status.Failed,
                Current = _status.Current,
                StartedUtc = _status.StartedUtc,
                FinishedUtc = _status.FinishedUtc,
                ReleaseSearchesUntilUtc = _nextReleaseSearchUtc > now ? _nextReleaseSearchUtc : null
            };
        }
    }

    /// <inheritdoc />
    public bool StartFullRun()
    {
        lock (_lock)
        {
            return !_status.Running && _fullRunRequests.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Fills in the given series one after another.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="kind">Which run it is.</param>
    /// <param name="pause">The pause after each series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task RunAsync(IReadOnlyList<Series> series, string kind, TimeSpan pause, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _status = new PopulateStatus { Running = true, Kind = kind, Total = series.Count, StartedUtc = _timeProvider.GetUtcNow().UtcDateTime };
        }

        _logger.LogInformation("Filling in {Count} series ({Kind} run)", series.Count, kind);
        try
        {
            foreach (var item in series)
            {
                lock (_lock)
                {
                    _status.Current = item.Name;
                }

                var filledIn = await FillInAsync(item, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _status.Done++;
                    if (!filledIn)
                    {
                        _status.Failed++;
                    }
                }

                await Task.Delay(pause, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _status.Running = false;
                _status.Current = null;
                _status.FinishedUtc = _timeProvider.GetUtcNow().UtcDateTime;
                _logger.LogInformation("{Kind} run done: {Done} series, {Failed} not found", kind, _status.Done, _status.Failed);
            }
        }
    }

    /// <summary>
    /// Gets the series the night run covers: added in the last 7 days, or still airing.
    /// </summary>
    /// <returns>The series.</returns>
    internal IReadOnlyList<Series> GetNightlySeries()
    {
        var since = _timeProvider.GetUtcNow().UtcDateTime - _recent;
        return GetAllSeries()
            .Where(s => s.DateCreated >= since || s.Status == SeriesStatus.Continuing)
            .ToList();
    }

    /// <summary>
    /// Gets every series in the Shows and Anime libraries.
    /// </summary>
    /// <returns>The series, by name.</returns>
    internal IReadOnlyList<Series> GetAllSeries()
    {
        var libraryIds = _libraryManager.GetVirtualFolders()
            .Where(f => PirateFinLibraries.Normalize(f.Name) is PirateFinLibraries.Shows or PirateFinLibraries.Anime)
            .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => !id.IsEmpty())
            .ToList();

        return libraryIds
            .SelectMany(id => _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = id,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Series]
            }))
            .OfType<Series>()
            .DistinctBy(s => s.Id)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wakes up for a full run request, or every minute to see whether the night window began.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(_checkEvery);
            try
            {
                await _fullRunRequests.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // A minute went by.
            }

            try
            {
                if (_fullRunRequests.Reader.TryRead(out _))
                {
                    await RunAsync(GetAllSeries(), FullRun, _pauseBetweenSeries, stoppingToken).ConfigureAwait(false);
                }
                else if (IsNightlyDue())
                {
                    _lastNightlyUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    await RunAsync(GetNightlySeries(), NightlyRun, _pauseBetweenSeries, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Filling in the libraries failed");
            }
        }
    }

    private bool IsNightlyDue()
        => StorageRebalancer.IsInMoveWindow(_options.CurrentValue, TimeOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime))
            && _timeProvider.GetUtcNow().UtcDateTime - _lastNightlyUtc >= _nightlyAtMostEvery;

    // The missing seasons / episodes, a metadata and image refresh, and the new episodes' release searches spaced out.
    private async Task<bool> FillInAsync(Series series, CancellationToken cancellationToken)
    {
        var found = false;
        try
        {
            found = await _searchService.RefreshSeriesAsync(series, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not find the missing seasons / episodes of {Series}: {Message}", series.Name, ex.Message);
        }

        _providerManager.QueueRefresh(
            series.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ForceSave = true,
                IsAutomated = true
            },
            RefreshPriority.Low);

        await SpaceOutReleaseSearchesAsync(series, cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>
    /// Gives each aired episode that was never searched for a release its own time, 30 seconds after the previous one,
    /// so the subscription search (which otherwise takes hundreds a minute) goes through them slowly.
    /// </summary>
    private async Task SpaceOutReleaseSearchesAsync(Series series, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode],
                IsInternet = true
            })
            .OfType<Episode>()
            .Where(e => e.Scheduled
                && !e.IsDownloaded
                && !e.DownloadStartedUtc.HasValue
                && string.IsNullOrEmpty(e.DownloadUrl)
                && !e.SourceSearchStartedUtc.HasValue
                && e.FirstAiring.HasValue
                && ReleaseStatusResolver.ToUtc(e.FirstAiring.Value) <= now
                && (!e.NextSourceSearchUtc.HasValue || e.NextSourceSearchUtc.Value <= now))
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber)
            .ToList();

        foreach (var episode in episodes)
        {
            lock (_lock)
            {
                _nextReleaseSearchUtc = (_nextReleaseSearchUtc > now ? _nextReleaseSearchUtc : now) + _releaseSearchSpacing;
                episode.NextSourceSearchUtc = _nextReleaseSearchUtc;
            }

            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }
}
