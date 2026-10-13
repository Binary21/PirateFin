using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

public sealed class EpisodeSubscriptionRepository : IEpisodeSubscriptionRepository
{
    private readonly ILibraryManager _libraryManager;

    private readonly ILogger<EpisodeSubscriptionRepository> _logger;

    public EpisodeSubscriptionRepository(
        ILibraryManager libraryManager,
        ILogger<EpisodeSubscriptionRepository> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task<IReadOnlyList<Episode>> GetCandidatesAsync(
        DateTime utcNow,
        int pageSize,
        int startIndex,
        CancellationToken ct)
    {
        if (pageSize <= 0 || ct.IsCancellationRequested)
        {
            return Task.FromResult<IReadOnlyList<Episode>>(Array.Empty<Episode>());
        }

        var normalizedStartIndex = Math.Max(0, startIndex);
        var rawBatchSize = Math.Max(pageSize * 4, 200);
        var rawStartIndex = 0;
        var matchedBeforePage = 0;
        var page = new List<Episode>(pageSize);

        while (!ct.IsCancellationRequested && page.Count < pageSize)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                Scheduled = true,
                OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Ascending) }
            });

            if (items.Count == 0)
            {
                break;
            }

            foreach (var episode in items.OfType<Episode>())
            {
                if (!(episode.IsInternet
                    && !episode.IsDownloaded
                    && !episode.IsDownloadable
                    && !episode.CantDownload
                    && string.IsNullOrWhiteSpace(episode.DownloadUrl)
                    && episode.FirstAiring.HasValue
                    && episode.FirstAiring.Value <= utcNow))
                {
                    continue;
                }

                if (matchedBeforePage < normalizedStartIndex)
                {
                    matchedBeforePage++;
                    continue;
                }

                page.Add(episode);
                if (page.Count >= pageSize)
                {
                    break;
                }
            }

            rawStartIndex += items.Count;

            if (items.Count < rawBatchSize)
            {
                break;
            }
        }

        _logger.LogInformation(
            "Returning {Count} released episodes (PageSize={PageSize}, StartIndex={StartIndex})",
            page.Count,
            pageSize,
            normalizedStartIndex);

        return Task.FromResult<IReadOnlyList<Episode>>(page);
    }

    public async Task PersistDownloadUrlAsync(Episode episode, EpisodeDownloadCandidate downloadCandidate, CancellationToken ct)
    {
        _logger.LogInformation(" Updating {Episode} Magnet URL, seeders and setting IsDownloadable status to True", episode.Name);
        episode.DownloadUrl = downloadCandidate.MagnetUrl;
        episode.Seeders = downloadCandidate.Seeders;
        episode.IsDownloadable = true;
        await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }

    public Task<bool> IsSeasonSubscribedAsync(Episode episode, CancellationToken ct)
    {
        var season = episode.Season;
        return Task.FromResult(season?.Subscribed == true);
    }
}
