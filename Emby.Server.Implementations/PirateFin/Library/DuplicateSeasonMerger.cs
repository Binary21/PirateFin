using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Merges seasons of a series that share a season number. They appear when a season folder loses its season (e.g. a
/// provider removed it) and Jellyfin makes a folderless "Season N" for the leftover episodes: the next scan of the
/// series finds the folder again and adds a second "Season N" next to it. The season with the folder is kept (every
/// scan would bring it back), the other's episodes are moved into it, and the other is removed. Every duplicate is
/// merged at startup; afterwards a series is merged shortly after a season is added to it (once its scan is done).
/// </summary>
public sealed class DuplicateSeasonMerger : BackgroundService
{
    private static readonly TimeSpan _settle = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DuplicateSeasonMerger> _logger;
    private readonly Channel<Guid> _addedTo = Channel.CreateUnbounded<Guid>();

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateSeasonMerger"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public DuplicateSeasonMerger(ILibraryManager libraryManager, TimeProvider timeProvider, ILogger<DuplicateSeasonMerger> logger)
    {
        _libraryManager = libraryManager;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Merges the duplicate seasons of every series.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many seasons were merged away.</returns>
    internal async Task<int> MergeAllAsync(CancellationToken cancellationToken)
    {
        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Season],
                Recursive = true
            })
            .OfType<Season>();
        return await MergeAsync(seasons, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the duplicate seasons of one series.
    /// </summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many seasons were merged away.</returns>
    internal async Task<int> MergeSeriesAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = seriesId,
                IncludeItemTypes = [BaseItemKind.Season]
            })
            .OfType<Season>();
        return await MergeAsync(seasons, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;

        try
        {
            var merged = await MergeAllAsync(stoppingToken).ConfigureAwait(false);
            if (merged > 0)
            {
                _logger.LogInformation("Merged {Count} duplicate seasons", merged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not merge the duplicate seasons");
        }

        while (await _addedTo.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            // Lets the scan that added the season finish before its series is touched.
            await Task.Delay(_settle, _timeProvider, stoppingToken).ConfigureAwait(false);

            var series = new HashSet<Guid>();
            while (_addedTo.Reader.TryRead(out var id))
            {
                series.Add(id);
            }

            foreach (var id in series)
            {
                try
                {
                    await MergeSeriesAsync(id, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not merge the duplicate seasons of series {Id}", id);
                }
            }
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Season { IndexNumber: not null } season && !season.SeriesId.IsEmpty())
        {
            _addedTo.Writer.TryWrite(season.SeriesId);
        }
    }

    private async Task<int> MergeAsync(IEnumerable<Season> seasons, CancellationToken cancellationToken)
    {
        var merged = 0;
        var duplicates = seasons
            .Where(s => s.IndexNumber.HasValue && !s.SeriesId.IsEmpty())
            .GroupBy(s => (s.SeriesId, s.IndexNumber))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in duplicates)
        {
            merged += await MergeGroupAsync(group.ToList(), cancellationToken).ConfigureAwait(false);
        }

        return merged;
    }

    private async Task<int> MergeGroupAsync(List<Season> group, CancellationToken cancellationToken)
    {
        // Two folders for one season number (e.g. "Season 1" and "Season 01") are the user's to sort out.
        var withFolder = group.Where(s => !string.IsNullOrEmpty(s.Path)).ToList();
        if (withFolder.Count > 1)
        {
            return 0;
        }

        var keeper = withFolder.FirstOrDefault()
            ?? group.OrderByDescending(s => GetEpisodes(s).Count).ThenBy(s => s.DateCreated).First();
        var keeperNumbers = GetEpisodes(keeper)
            .Where(e => e.IndexNumber.HasValue)
            .Select(e => e.IndexNumber!.Value)
            .ToHashSet();

        var merged = 0;
        foreach (var other in group.Where(s => !s.Id.Equals(keeper.Id)))
        {
            _logger.LogInformation("Merging a duplicate {Season} of {Series} into the one at {Path}", other.Name, other.SeriesName, keeper.Path);
            foreach (var episode in GetEpisodes(other))
            {
                // A placeholder for an episode the kept season already has is just a second copy.
                if (episode.IndexNumber is { } number && keeperNumbers.Contains(number) && IsPlaceholder(episode))
                {
                    _libraryManager.DeleteItem(episode, new DeleteOptions { DeleteFileLocation = false }, false);
                    continue;
                }

                if (episode.ParentId.Equals(other.Id))
                {
                    episode.ParentId = keeper.Id;
                }

                episode.SeasonId = keeper.Id;
                episode.SeasonName = keeper.Name;
                await _libraryManager.UpdateItemAsync(episode, keeper, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                if (episode.IndexNumber.HasValue)
                {
                    keeperNumbers.Add(episode.IndexNumber.Value);
                }
            }

            // The subscription and schedule live on the season.
            var changed = false;
            if (other.Subscribed && !keeper.Subscribed)
            {
                keeper.Subscribed = true;
                changed = true;
            }

            if (other.Scheduled && !keeper.Scheduled)
            {
                keeper.Scheduled = true;
                changed = true;
            }

            if (other.IsInternet && !keeper.IsInternet)
            {
                keeper.IsInternet = true;
                changed = true;
            }

            if (string.IsNullOrEmpty(keeper.Overview) && !string.IsNullOrEmpty(other.Overview))
            {
                keeper.Overview = other.Overview;
                changed = true;
            }

            if (changed && _libraryManager.GetItemById(keeper.SeriesId) is Series series)
            {
                await _libraryManager.UpdateItemAsync(keeper, series, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            // Deleting a season deletes what's under it, so it must be empty by now.
            if (GetEpisodes(other).Count > 0)
            {
                _logger.LogWarning("Not removing the duplicate {Season} of {Series}: it still has episodes", other.Name, other.SeriesName);
                continue;
            }

            _libraryManager.DeleteItem(other, new DeleteOptions { DeleteFileLocation = false }, false);
            merged++;
        }

        return merged;
    }

    private static bool IsPlaceholder(Episode episode)
        => episode.IsPlaceHolder && !episode.IsDownloaded && !episode.DownloadStartedUtc.HasValue;

    // The episodes under a season, or linked to it by season id (a series without season folders keeps them under the series).
    private List<Episode> GetEpisodes(Season season)
        => _libraryManager.GetItemList(new InternalItemsQuery { ParentId = season.Id })
            .Concat(_libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [season.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true
            }))
            .OfType<Episode>()
            .DistinctBy(e => e.Id)
            .ToList();
}
